using AForge.Video;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.MediaFoundation;
using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 三条采集后端的帧到达回调。
    ///
    /// 放在一起是因为它们必须遵守同一条不变式：<b>旋转发生在写入预录缓冲之前</b>。
    /// 预录帧与实时帧同源，任何一条路径漏转或重复转，录像开头几秒就会与后面方向不同
    /// （现场反馈过"开启旋转后预录那几秒是倒的"）。
    ///
    /// - Media Foundation：旋转在采集源内部完成（GPU 着色器优先，失败回退 CPU）。
    /// - DirectShow / 网络摄像头：在这里按配置角度转，再进预录缓冲与主帧槽。
    /// </summary>
    public partial class MainViewModel
    {
        /// <summary>新后端的帧到达。与 AForge 路径共用同一套限流、预录与录像逻辑。</summary>
        private void MfCameraSource_FrameReady(object sender, MfFrameEventArgs e)
        {
            _lastFrameTime = DateTime.Now;
            Interlocked.Increment(ref _framesSinceCameraSessionStart);
            MarkCameraStreamHealthy();
            Interlocked.Exchange(ref _archiveFrameUtcTicks, DateTime.UtcNow.Ticks);
            UpdateCameraSourceFpsEstimate();

            try
            {
                // 帧已经是 BGR24，不需要 BitmapToMat 那次格式转换与克隆，
                // 也不需要事后的色度校正（新后端在解码时就用了正确的矩阵）。
                // 旋转已经在采集源内部完成，这里直接用。
                Mat frame = e.Frame;
                if (ShouldCaptureEventRecordingBufferFrame())
                    UpdatePreRecordBuffer(frame);
                HandleCameraFrame(frame);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "Media Foundation frame processing failed", ex);
            }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            _lastFrameTime = DateTime.Now;
            Interlocked.Increment(ref _framesSinceCameraSessionStart);
            MarkCameraStreamHealthy();
            Interlocked.Exchange(ref _archiveFrameUtcTicks, DateTime.UtcNow.Ticks);
            UpdateCameraSourceFpsEstimate();

            try
            {
                Mat frame = BitmapToMat(eventArgs.Frame);
                // 旋转必须在写入预录缓冲之前完成：预录帧与实时帧同源，
                // 否则注入时还要再转一次，两条路径的角度一旦漂移就会录出倒的画面。
                frame = CameraFrameOrientation.Apply(frame, Config.CameraRotationDegrees);
                if (ShouldCaptureEventRecordingBufferFrame())
                    UpdatePreRecordBuffer(frame);
                HandleCameraFrame(frame);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "NewFrame conversion failed", ex);
            }
        }

        private void NetworkCameraSource_FrameReady(object sender, NetworkCameraFrameEventArgs e)
        {
            _lastFrameTime = DateTime.Now;
            Interlocked.Increment(ref _framesSinceCameraSessionStart);
            MarkCameraStreamHealthy();
            Interlocked.Exchange(ref _archiveFrameUtcTicks, DateTime.UtcNow.Ticks);
            UpdateCameraSourceFpsEstimate();

            try
            {
                // 与 DirectShow 路径同一口径：先旋转，再进预录缓冲与主帧槽。
                Mat frame = CameraFrameOrientation.Apply(e.Frame, Config.CameraRotationDegrees);
                if (ShouldCaptureEventRecordingBufferFrame())
                    UpdatePreRecordBuffer(frame);
                HandleCameraFrame(frame);
            }
            catch (Exception ex)
            {
                // 这条回调跑在 ffmpeg 读帧线程上：抛出去会一路冒到读循环，
                // 让整个网络流静默停掉（现场只看到"信号丢失 20s"反复重连）。
                // 另外两条采集路径一直有兜底，这里补齐。
                RuntimeLog.Error("Camera", "Network camera frame processing failed", ex);
            }
        }
    }
}
