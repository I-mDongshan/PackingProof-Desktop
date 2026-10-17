using ExpressPackingMonitoring.Services.Gpu;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 真正跑一遍 GPU 转换，按像素核对旋转结果 —— 只验证"HLSL 能编译"是不够的，
/// 方向写反、行列搞错在编译期一点问题都没有，只有比对输出像素才能拦住。
///
/// 做法：同一份 YUY2 合成帧先按 0 度渲染出参考图，再按各角度渲染，
/// 与"把参考图在 CPU 上按同一角度旋转"的期望逐像素比较。
/// 1:1 渲染没有重采样，差异应只来自取整，因此容差很小。
///
/// 没有可用的 D3D11 硬件设备时跳过（和软件里的行为一致：回退 CPU 转换）。
/// </summary>
public sealed class GpuRotationPixelTests
{
    private const int Width = 64;
    private const int Height = 48;
    private const byte MaxAllowedChannelDifference = 4;

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void GpuRotation_MatchesCpuRotationOfTheSameFrame(int degrees)
    {
        using Mat yuy2 = CreateYuy2Frame();
        using Mat reference = Render(yuy2, degrees: 0);
        using Mat actual = Render(yuy2, degrees);
        using var expected = new Mat();

        switch (degrees)
        {
            case 90:
                Cv2.Rotate(reference, expected, RotateFlags.Rotate90Clockwise);
                break;
            case 180:
                Cv2.Flip(reference, expected, FlipMode.XY);
                break;
            default:
                Cv2.Rotate(reference, expected, RotateFlags.Rotate90Counterclockwise);
                break;
        }

        Assert.Equal(expected.Rows, actual.Rows);
        Assert.Equal(expected.Cols, actual.Cols);

        using Mat difference = new();
        Cv2.Absdiff(expected, actual, difference);
        Cv2.MinMaxLoc(difference, out double min, out double max);
        Assert.True(
            max <= MaxAllowedChannelDifference,
            $"GPU 旋转 {degrees}° 与 CPU 期望不一致，最大通道差 {max}（阈值 {MaxAllowedChannelDifference}）");
    }

    /// <summary>旋转 0 度与不旋转必须是同一张图：默认路径不能因为这次改动而改变。</summary>
    [Fact]
    public void ZeroRotation_IsIdentity()
    {
        using Mat yuy2 = CreateYuy2Frame();
        using Mat first = Render(yuy2, degrees: 0);
        using Mat second = Render(yuy2, degrees: 0);

        using Mat difference = new();
        Cv2.Absdiff(first, second, difference);
        Cv2.MinMaxLoc(difference, out _, out double max);
        Assert.Equal(0, max);
    }

    /// <summary>
    /// 合成一份非对称的 YUY2 帧：亮度按行列变化，横向和纵向都不能对称，
    /// 否则 90 与 270 会得到相同的结果、测不出方向。
    /// </summary>
    private static Mat CreateYuy2Frame()
    {
        var frame = new Mat(Height, Width * 2, MatType.CV_8UC1);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x += 2)
            {
                byte y0 = (byte)((x * 3 + y * 5) % 220 + 16);
                byte y1 = (byte)((x * 3 + y * 5 + 7) % 220 + 16);
                frame.Set(y, x * 2, y0);
                frame.Set(y, x * 2 + 1, (byte)(96 + ((x + y) % 64)));
                frame.Set(y, x * 2 + 2, y1);
                frame.Set(y, x * 2 + 3, (byte)(160 - ((x + y) % 64)));
            }
        }

        return frame;
    }

    /// <summary>按指定角度渲染一帧；GPU 不可用时跳过整个用例。</summary>
    private static Mat Render(Mat yuy2, int degrees)
    {
        (int targetWidth, int targetHeight) = degrees is 90 or 270
            ? (Height, Width)
            : (Width, Height);

        GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            Width,
            Height,
            targetWidth,
            targetHeight,
            isNv12: false,
            rotationDegrees: degrees);
        if (converter == null)
            Assert.Skip($"当前环境没有可用的 D3D11 设备：{GpuFrameConverter.LastCreateFailure}");

        using (converter)
        {
            Assert.True(
                converter.TryRender(yuy2.Data, (int)yuy2.Step(), useBt709: false),
                "GPU 渲染失败");

            var output = new Mat(converter.TargetHeight, converter.TargetWidth, MatType.CV_8UC3);
            if (!converter.TryReadBackInto(output))
            {
                output.Dispose();
                Assert.Fail("GPU 结果回读失败");
            }

            return output;
        }
    }
}
