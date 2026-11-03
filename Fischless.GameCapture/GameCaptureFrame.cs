using OpenCvSharp;
using Vanara.PInvoke;

namespace Fischless.GameCapture;

public sealed class GameCaptureFrame : IDisposable
{
    public GameCaptureFrame(Mat frame, RECT? captureRect = null)
    {
        Frame = frame;
        CaptureRect = captureRect;
    }

    public Mat Frame { get; }

    public RECT? CaptureRect { get; }

    /// <summary>缓存型截图后端提供同一原始帧的标识；直接截图可留空。</summary>
    public long? FrameIdentity { get; init; }

    public void Dispose()
    {
        Frame.Dispose();
        GC.SuppressFinalize(this);
    }
}
