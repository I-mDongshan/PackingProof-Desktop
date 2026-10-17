using ExpressPackingMonitoring.Services;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraFrameOrientationTests
{
    [Fact]
    public void Apply_WhenDisabled_PreservesFrame()
    {
        using var frame = CreateTestFrame();

        CameraFrameOrientation.Apply(frame, rotate180: false);

        Assert.Equal(1, frame.At<byte>(0, 0));
        Assert.Equal(4, frame.At<byte>(1, 1));
    }

    [Fact]
    public void Apply_WhenEnabled_RotatesFrameWithoutChangingSize()
    {
        using var frame = CreateTestFrame();

        CameraFrameOrientation.Apply(frame, rotate180: true);

        Assert.Equal(2, frame.Rows);
        Assert.Equal(2, frame.Cols);
        Assert.Equal(4, frame.At<byte>(0, 0));
        Assert.Equal(3, frame.At<byte>(0, 1));
        Assert.Equal(2, frame.At<byte>(1, 0));
        Assert.Equal(1, frame.At<byte>(1, 1));
    }

    /// <summary>
    /// 主摄与副摄都要能按 0/90/180/270 四档设置：横装、竖装、倒装都能直接摆正，
    /// 而不是只有一个"倒装"开关。
    /// </summary>
    [Fact]
    public void Settings_ExposesAllFourRotationAnglesForBothCameras()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "src", "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml"));

        Assert.Contains("Text=\"画面旋转\"", xaml, StringComparison.Ordinal);
        // 主摄与每一路叠加画面共用同一个标签与同一套角度选项
        Assert.Contains("{Binding Config.CameraRotationDegrees", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding RotationDegrees, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"90\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"270\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// 90/270 必须交换宽高，并且返回新的 Mat、释放旧帧：
    /// 调用方若继续用旧引用，录到的就是旋转前的画面。
    /// </summary>
    [Fact]
    public void Apply_WithRightAngle_SwapsDimensionsAndReleasesOriginal()
    {
        Mat frame = CreateRectFrame();
        using Mat rotated = CameraFrameOrientation.Apply(frame, 90);

        Assert.True(frame.IsDisposed, "转置必须释放原帧，避免调用方继续持有旧画面");
        Assert.Equal(3, rotated.Rows);
        Assert.Equal(2, rotated.Cols);
        Assert.Equal(4, rotated.At<byte>(0, 0));
        Assert.Equal(1, rotated.At<byte>(0, 1));
        Assert.Equal(6, rotated.At<byte>(2, 0));
        Assert.Equal(3, rotated.At<byte>(2, 1));
    }

    [Fact]
    public void Apply_With270_RotatesTheOtherWay()
    {
        Mat frame = CreateRectFrame();
        using Mat rotated = CameraFrameOrientation.Apply(frame, 270);

        Assert.Equal(3, rotated.Rows);
        Assert.Equal(2, rotated.Cols);
        Assert.Equal(3, rotated.At<byte>(0, 0));
        Assert.Equal(6, rotated.At<byte>(0, 1));
        Assert.Equal(1, rotated.At<byte>(2, 0));
        Assert.Equal(4, rotated.At<byte>(2, 1));
    }

    /// <summary>录制参数按旋转后的尺寸走，所以这个换算必须是唯一口径。</summary>
    [Theory]
    [InlineData(0, 3, 2)]
    [InlineData(90, 2, 3)]
    [InlineData(180, 3, 2)]
    [InlineData(270, 2, 3)]
    public void RotateDimensions_SwapsOnlyForRightAngles(int degrees, int expectedWidth, int expectedHeight)
    {
        (int width, int height) = CameraFrameOrientation.RotateDimensions(3, 2, degrees);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    private static Mat CreateRectFrame()
    {
        var frame = new Mat(2, 3, MatType.CV_8UC1);
        frame.Set(0, 0, (byte)1);
        frame.Set(0, 1, (byte)2);
        frame.Set(0, 2, (byte)3);
        frame.Set(1, 0, (byte)4);
        frame.Set(1, 1, (byte)5);
        frame.Set(1, 2, (byte)6);
        return frame;
    }

    private static Mat CreateTestFrame()
    {
        var frame = new Mat(2, 2, MatType.CV_8UC1);
        frame.Set(0, 0, (byte)1);
        frame.Set(0, 1, (byte)2);
        frame.Set(1, 0, (byte)3);
        frame.Set(1, 1, (byte)4);
        return frame;
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string candidate = Path.Combine([current.FullName, .. relativeParts]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
