using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace WinScreen.Pip;

/// <summary>
/// Windows draws a yellow border around a window while it's being captured. Newer Windows lets an app ask
/// to leave it off, and the person can refuse in Settings → Privacy &amp; security → Screenshot borders. WinScreen
/// asks once, off the UI thread, and keeps the border whenever the answer isn't a plain yes.
/// </summary>
internal static class CaptureBorder
{
    private const string SessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const string AccessType = "Windows.Graphics.Capture.GraphicsCaptureAccess";
    private const int AnswerWaitMilliseconds = 1000;
    private static readonly object Gate = new();
    private static Task<bool>? _answer;

    /// <summary>Whether the person wants the border left off. Set from Settings.</summary>
    internal static bool Wanted { get; set; } = true;

    /// <summary>Asks Windows for permission. Replaced in tests.</summary>
    internal static Func<Task<bool>> Ask { get; set; } = AskWindows;

    /// <summary>Whether this Windows can leave the border off at all.</summary>
    internal static Func<bool> Supported { get; set; } = () =>
        ApiInformation.IsPropertyPresent(SessionType, "IsBorderRequired") && ApiInformation.IsTypePresent(AccessType);

    /// <summary>
    /// Leaves the border off this session if the person wants that and Windows allows it. Call before
    /// StartCapture, off the UI thread. Returns whether the border is off; any doubt keeps it on.
    /// </summary>
    /// <param name="wait">How long to wait for Windows' answer if it hasn't come yet. Zero on the UI thread.</param>
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

    /// <summary>Asks Windows now, without waiting, so the answer is ready by the time a capture starts.</summary>
    internal static Task<bool> Prepare()
    {
        lock (Gate) return _answer ??= Task.Run(Ask);
    }

    /// <summary>Forgets the answer, for tests and for after the person changes the Windows setting.</summary>
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
