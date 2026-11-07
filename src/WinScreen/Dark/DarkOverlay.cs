using static WinScreen.Settings.UiText;
using WinScreen.Native;

namespace WinScreen.Dark;

/// <summary>
/// A click-through window sitting exactly over one target window, showing what is underneath with
/// light and dark swapped and colours kept. The target itself isn't changed in any way.
/// </summary>
internal sealed class DarkOverlay : Form
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;

    /// <summary>Swaps light and dark without shifting hues.</summary>
    private static readonly float[] SmartInversion =
    [
        0.3333f, -0.6667f, -0.6667f, 0, 0,
        -0.6667f, 0.3333f, -0.6667f, 0, 0,
        -0.6667f, -0.6667f, 0.3333f, 0, 0,
        0, 0, 0, 1, 0,
        1, 1, 1, 0, 1,
    ];

    private static bool _initialized;

    private readonly nint _target;
    private readonly Windows.WindowIdentity _identity;
    private readonly bool _pinned;
    private nint _magnifier;
    private Rectangle? _shape;

    /// <param name="target">The window to cover.</param>
    /// <param name="pinned">
    /// Whether to join the always-on-top windows. Windows only honours this for an overlay when it is
    /// created, so a target that gets pinned or unpinned needs a new overlay.
    /// </param>
    public DarkOverlay(nint target, bool pinned)
    {
        _target = target;
        _identity = Windows.WindowIdentity.Capture(target);
        _pinned = pinned;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Text = T("WinScreen dark overlay");
    }

    /// <summary>False once the target has been pinned or unpinned and this overlay is in the wrong group.</summary>
    public bool MatchesTarget => IsPinned(_target) == _pinned;

    public bool TargetAlive => _identity.Matches();

    public static bool IsPinned(nint window)
    {
        return (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE |
                                        User32.WS_EX_TOOLWINDOW);
            if (_pinned)
                parameters.ExStyle |= (int)User32.WS_EX_TOPMOST;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_initialized)
            _initialized = Magnification.MagInitialize();

        User32.SetLayeredWindowAttributes(Handle, 0, 255, User32.LWA_ALPHA);
        _magnifier = User32.CreateWindowEx(0, Magnification.ControlClass, "", WS_CHILD | WS_VISIBLE, 0, 0, Width,
            Height, Handle, 0, 0, 0);
        if (_magnifier == 0)
            throw new Windows.WindowOperationException(
                T("Windows didn't provide the screen filter that dark mode needs."));

        unsafe
        {
            fixed (float* matrix = SmartInversion)
                Magnification.MagSetColorEffect(_magnifier, matrix);
        }
    }

    /// <summary>Leaves these windows out of what is filtered, including every overlay.</summary>
    public unsafe void SetIgnoredWindows(nint[] windows)
    {
        fixed (nint* list = windows)
            Magnification.MagSetWindowFilterList(_magnifier, Magnification.MW_FILTERMODE_EXCLUDE, windows.Length, list);
    }

    /// <summary>Follows the target and redraws. Returns false once the target has closed.</summary>
    public bool Follow()
    {
        if (!_identity.Matches())
            return false;

        // The frame rather than the window rectangle, whose invisible margins would invert the screen around it.
        if (!Windows.WindowInfo.IsAvailable(_target) || Windows.WindowInfo.IsConcealed(_target) ||
            !Windows.WindowInfo.TryGetFrameBounds(_target, out Rectangle bounds, out Rectangle whole))
        {
            Hide();
            return true;
        }

        if (Bounds != bounds)
        {
            Bounds = bounds;
            User32.SetWindowPos(_magnifier, 0, 0, 0, bounds.Width, bounds.Height,
                User32.SWP_NOACTIVATE | User32.SWP_NOZORDER);
        }

        FollowShape(whole.Left - bounds.Left, whole.Top - bounds.Top);
        if (!Visible)
            Show();

        StayJustAboveTarget();
        Magnification.MagSetWindowSource(_magnifier, new Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        User32.InvalidateRect(_magnifier, 0, true);
        return true;
    }

    /// <summary>A cropped window shows nothing outside the crop, so neither does its overlay.</summary>
    private void FollowShape(int offsetX, int offsetY)
    {
        Rectangle? shape = Windows.WindowInfo.ShapeOf(_target);
        if (shape is { } window)
        {
            window.Offset(offsetX, offsetY);
            shape = window;
        }

        if (shape == _shape)
            return;

        _shape = shape;
        Region? previous = Region;
        Region = shape is { } part ? new Region(part) : null;
        previous?.Dispose();
    }

    private void StayJustAboveTarget()
    {
        nint above = User32.GetWindow(_target, User32.GW_HWNDPREV);
        if (above == Handle)
            return;

        nint insertAfter = above;
        User32.SetWindowPos(Handle, insertAfter, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }
}
