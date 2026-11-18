using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace WinScreen.Pip;

// Newer Windows lets an app ask to leave off the yellow capture border, and the person can refuse in Windows Settings.
// Asked once, off the UI thread; the border stays whenever the answer isn't a plain yes.
internal static class CaptureBorder
{
    private const string SessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const string AccessType = "Windows.Graphics.Capture.GraphicsCaptureAccess";
    private const int AnswerWaitMilliseconds = 1000;
    private static readonly object Gate = new();
    private static Task<bool>? _answer;

    internal static bool Wanted { get; set; } = true;

    internal static Func<Task<bool>> Ask { get; set; } = AskWindows;

    internal static Func<bool> Supported { get; set; } = () =>
        ApiInformation.IsPropertyPresent(SessionType, "IsBorderRequired") && ApiInformation.IsTypePresent(AccessType);

    // Call before StartCapture, off the UI thread (wait 0 on it). Any doubt keeps the border.
    internal static bool TryHide(GraphicsCaptureSession session, int wait = AnswerWaitMilliseconds) =>
        TryHide(value => session.IsBorderRequired = value, wait);

    internal static bool TryHide(Action<bool> setBorderRequired, int wait = AnswerWaitMilliseconds)
    {
        if (!Wanted || !Supported()) return false;
        Task<bool> answer = Prepare();
        try
        {
            // A slow answer keeps the border for this view rather than holding up the picture; later views use it.
            if (!answer.Wait(wait) || !answer.Result) return false;
            setBorderRequired(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static Task<bool> Prepare()
    {
        lock (Gate) return _answer ??= Task.Run(Ask);
    }

    internal static void Forget()
    {
        lock (Gate) _answer = null;
    }

    private static async Task<bool> AskWindows()
    {
        try
        {
            AppCapabilityAccessStatus status =
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
            return status == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
