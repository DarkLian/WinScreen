using WinScreen.Native;

namespace WinScreen.Pip;

public enum InputRoute
{
    Messages,
    // Surfaces that read the pointer from the system and ignore posted mouse messages, such as WinUI islands and Store
    // app content, which may be another process inside the original's frame.
    Automation,
    Refused,
}

public enum InputOperation { Move, Click, DoubleClick, ContextClick, OtherButton, Drag, Wheel }

// Surfaces are recognised by their UI framework's window class, never by the app.
internal static class PipInputRoutes
{
    internal static InputRoute RouteFor(string className, bool sameProcess, bool insideOriginal) =>
        !insideOriginal ? InputRoute.Refused
        : Compatibility.AppProfiles.SurfaceRoute(className) is { } route ? route
        : sameProcess ? InputRoute.Messages
        : InputRoute.Refused;

    internal static InputRoute RouteFor(nint target, nint original, uint originalProcess)
    {
        if (target == 0 || !User32.IsWindow(target)) return InputRoute.Refused;
        User32.GetWindowThreadProcessId(target, out uint process);
        bool inside = target == original || User32.GetAncestor(target, User32.GA_ROOT) == original;
        return RouteFor(Windows.WindowInfo.ClassName(target), process == originalProcess, inside);
    }

    // Automation has no pointer: it can act on a control and scroll, but not hover, drag or take other buttons.
    internal static bool Supports(InputRoute route, InputOperation operation) => route switch
    {
        InputRoute.Messages => true,
        InputRoute.Automation => operation is InputOperation.Click or InputOperation.DoubleClick or InputOperation.ContextClick or InputOperation.Wheel,
        _ => false,
    };
}
