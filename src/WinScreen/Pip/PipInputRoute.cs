using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>How input reaches a window inside a hidden original.</summary>
public enum InputRoute
{
    /// <summary>Posted mouse messages at the mapped point: ordinary windows of the original's own program.</summary>
    Messages,
    /// <summary>
    /// UI Automation: surfaces that read the pointer from the system and ignore posted mouse messages, such as
    /// WinUI islands and Store app content, which may belong to another process inside the original's frame.
    /// </summary>
    Automation,
    /// <summary>Nothing is sent: a window of another program that isn't one of the surfaces above.</summary>
    Refused,
}

/// <summary>What the pointer did, as far as the route is concerned.</summary>
public enum InputOperation { Move, Click, DoubleClick, ContextClick, OtherButton, Drag, Wheel }

/// <summary>
/// The one place that decides how input reaches a window, and which operations that way supports. Surfaces are
/// recognised by their UI framework's window class, never by the app.
/// </summary>
internal static class PipInputRoutes
{
    private static readonly HashSet<string> AutomationSurfaces = new(StringComparer.Ordinal)
    {
        "Microsoft.UI.Content.DesktopChildSiteBridge", // WinUI 3 content in a desktop window
        "Windows.UI.Core.CoreWindow",                  // Store app content, in the app's own process
        "ApplicationFrameInputSinkWindow",             // The frame's input window over Store app content
    };

    /// <param name="className">The target window's class.</param>
    /// <param name="sameProcess">The target belongs to the original's process.</param>
    /// <param name="insideOriginal">The target is the original or one of its child windows.</param>
    internal static InputRoute RouteFor(string className, bool sameProcess, bool insideOriginal) =>
        insideOriginal && AutomationSurfaces.Contains(className) ? InputRoute.Automation
        : sameProcess && insideOriginal ? InputRoute.Messages
        : InputRoute.Refused;

    internal static InputRoute RouteFor(nint target, nint original, uint originalProcess)
    {
        if (target == 0 || !User32.IsWindow(target)) return InputRoute.Refused;
        User32.GetWindowThreadProcessId(target, out uint process);
        bool inside = target == original || User32.GetAncestor(target, User32.GA_ROOT) == original;
        return RouteFor(Windows.WindowInfo.ClassName(target), process == originalProcess, inside);
    }

    /// <summary>
    /// Whether the route can carry the operation at all. Automation has no pointer: it can act on a control (a click,
    /// a context menu, and a double-click only where that means two clicks) and scroll what scrolls, but not hover,
    /// drag or other buttons.
    /// </summary>
    internal static bool Supports(InputRoute route, InputOperation operation) => route switch
    {
        InputRoute.Messages => true,
        InputRoute.Automation => operation is InputOperation.Click or InputOperation.DoubleClick or InputOperation.ContextClick or InputOperation.Wheel,
        _ => false,
    };
}
