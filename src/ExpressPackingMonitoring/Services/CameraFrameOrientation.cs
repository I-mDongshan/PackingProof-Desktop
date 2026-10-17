using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

internal static class CameraFrameOrientation
{
    /// <summary>只认 0/90/180/270，其余（含哨兵值）一律按不旋转处理。</summary>
    internal static int NormalizeDegrees(int degrees) => degrees switch
    {
        90 => 90,
        180 => 180,
        270 => 270,
        _ => 0,
    };

    /// <summary>
    /// 旋转后画面的宽高。90/270 会交换宽高，录制参数与落位必须用这个结果，
    /// 否则编码器会因为"帧尺寸与约定不符"整帧丢弃。
    /// </summary>
    internal static (int Width, int Height) RotateDimensions(int width, int height, int degrees) =>
        NormalizeDegrees(degrees) is 90 or 270 ? (height, width) : (width, height);

    /// <summary>
    /// 按 0/90/180/270 旋转。
    ///
    /// 180 度是原地翻折，返回原帧；90/270 需要转置，OpenCV 不支持原地转置，
    /// 会返回一个新的 Mat 并释放旧帧，调用方**必须**使用返回值。
    /// 这样调用点不会出现"以为转过、其实拿到的是旧帧"的隐蔽错误。
    /// </summary>
    internal static Mat Apply(Mat frame, int degrees)
    {
        if (frame is null || frame.IsDisposed || frame.Empty())
            return frame!;

        switch (NormalizeDegrees(degrees))
        {
            case 90:
                return RotateAndRelease(frame, RotateFlags.Rotate90Clockwise);
            case 180:
                Cv2.Flip(frame, frame, FlipMode.XY);
                return frame;
            case 270:
                return RotateAndRelease(frame, RotateFlags.Rotate90Counterclockwise);
            default:
                return frame;
        }
    }

    /// <summary>旧调用口径：true 等价于 180 度。</summary>
    internal static Mat Apply(Mat frame, bool rotate180) => Apply(frame, rotate180 ? 180 : 0);

    private static Mat RotateAndRelease(Mat frame, RotateFlags flags)
    {
        var rotated = new Mat();
        Cv2.Rotate(frame, rotated, flags);
        frame.Dispose();
        return rotated;
    }
}
