using WinScreen.Native;
using WinScreen.Settings;

namespace WinScreen.Windows;

/// <summary>A red frame around every pinned window, keeping up with them as they move.</summary>
public sealed class PinBorders : IDisposable
{
    private readonly Dictionary<nint, PinBorder> _borders = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private PinBorderPreferences _preferences = new();

    public void Configure(PinBorderPreferences preferences)
    {
        _preferences = preferences.Validated();
        foreach (PinBorder border in _borders.Values) border.Configure(_preferences);
    }

    public PinBorders()
    {
        _timer.Tick += (_, _) => FollowAll();
    }

    /// <summary>Makes these windows, and no others, the ones with a frame.</summary>
    public void Show(IReadOnlyCollection<nint> windows)
    {
        foreach ((nint window, PinBorder border) in _borders.ToArray())
        {
            if (!windows.Contains(window))
            {
                _borders.Remove(window);
                border.Dispose();
            }
        }

        foreach (nint window in windows)
        {
            if (!_borders.ContainsKey(window))
            {
                var border = new PinBorder(window);
                border.Configure(_preferences);
                _borders[window] = border;
            }
        }

        _timer.Enabled = _borders.Count > 0;
    }

    public void Dispose()
    {
        Show([]);
        _timer.Dispose();
    }

    private void FollowAll()
    {
        foreach (PinBorder border in _borders.Values)
            border.Follow();
    }
}

/// <summary>Click-through and never active, so to everything but the eye it isn't there.</summary>
internal sealed class PinBorder : Form
{
    private PinBorderPreferences _preferences = new();
    private int _lastDpi;

    private readonly nint _target;
    private readonly WindowIdentity _identity;

    public PinBorder(nint target)
    {
        _target = target;
        _identity = WindowIdentity.Capture(target);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(232, 32, 32);
        Text = "WinScreen pin border";
        _ = Handle;
        Follow();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE
                                        | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST);
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        User32.SetLayeredWindowAttributes(Handle, 0, 255, User32.LWA_ALPHA);
    }

    public void Follow()
    {
        if (!_identity.Matches() || !WindowInfo.IsAvailable(_target) ||
            !WindowInfo.TryGetVisibleBounds(_target, out Rectangle bounds))
        {
            Hide();
            return;
        }

        int dpi = Dpi.Of(_target);
        if (Bounds != bounds || dpi != _lastDpi)
        {
            _lastDpi = dpi;
            Bounds = bounds;
            CutOutTheMiddle(bounds.Size);
        }

        if (!Visible)
            Show();

        StayJustAboveTarget();
    }

    private void CutOutTheMiddle(Size size)
    {
        int thickness = Dpi.Scale(_preferences.Thickness, _lastDpi == 0 ? 96 : _lastDpi);
        var whole = new Rectangle(Point.Empty, size);
        var frame = new Region(whole);
        frame.Exclude(Rectangle.Inflate(whole, -thickness, -thickness));

        Region? previous = Region;
        Region = frame;
        previous?.Dispose();
    }

    internal void Configure(PinBorderPreferences preferences)
    {
        _preferences = preferences.Validated();
        BackColor = _preferences.Color;
        User32.SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(_preferences.OpacityPercent * 255 / 100.0), User32.LWA_ALPHA);
        CutOutTheMiddle(Size);
    }

    /// <summary>Just above rather than above everything, so a pinned window under another one doesn't show through.</summary>
    private void StayJustAboveTarget()
    {
        nint above = User32.GetWindow(_target, User32.GW_HWNDPREV);
        if (above == Handle)
            return;

        nint insertAfter = above == 0 ? User32.HWND_TOPMOST : above;
        User32.SetWindowPos(Handle, insertAfter, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        if (insertAfter == User32.HWND_TOPMOST) TopLayer.KeepAbove();
    }
}
