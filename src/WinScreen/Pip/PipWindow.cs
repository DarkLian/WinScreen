using static WinScreen.Localization.UiText;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>
/// A borderless, always-on-top live view of another window. Drag to move, mouse wheel to resize,
/// Ctrl+drag to crop, Ctrl+double-click to remove the crop. Clicking activates the original by
/// default; verified input forwarding requires an explicit experimental opt-in. Using the view never
/// takes the focus from the app in use.
/// </summary>
public sealed class PipWindow : Form
{
    private const int WheelStep = 40;
    private const int DragThreshold = 4;
    private const int LeaveMargin = 8;
    private const int WheelNotch = 120;
    private const int StartLongSide = 480;
    private const int ScreenMargin = 20;
    private const int CloseButtonSize = 28;
    private const int EscapeHotkey = 0x5750;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int MinimumExclusionBuild = 19041;
    private const double ZoomStep = 1.25;
    private const int SmallLongSide = 320;
    private const int MediumLongSide = StartLongSide;
    private const int LargeLongSide = 720;

    private static readonly Color Accent = Color.FromArgb(79, 140, 255);

    private readonly PipSource _origin;
    private readonly nint _source;
    private readonly WindowController _windows;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _autoMove = new() { Interval = 1000 };
    private readonly ToolStripMenuItem _hideItem;
    private readonly ToolStripMenuItem _switcherItem;
    private readonly ToolStripItem _goToItem;
    private readonly ToolStripMenuItem _autoMoveItem;
    private readonly ToolStripMenuItem _removeCropItem;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private PipCloseButton? _closeButton;
    private PipTitleBar? _titleBar;
    private PipHoverPreview? _hoverPreview;
    private PipViewPreferences _viewPreferences = new();
    private Point _hoverPoint;
    private long _hoverSince;
    private Icon? _sourceIcon;
    private bool _suspended;
    private Task _retiringCapture = Task.CompletedTask;
    private bool _resumeRequested;
    private Bitmap? _taskbarPicture;
    private PipStateChip? _chip;
    private PipBackendSelector? _thumbnail;
    private PipPreferences _preferences = new() { Backend = PipBackendPreference.Dwm };
    private PipPreferences _global = new() { Backend = PipBackendPreference.Dwm };
    private PipOverrides _overrides = new();
    private RectangleF _zoom = PipGeometry.WholeView;
    private bool _panning;
    private Point _panFrom;
    private ToolStripMenuItem _retryItem = null!;
    private Rectangle _crop = Rectangle.Empty;
    private Rectangle _whole = Rectangle.Empty;
    private Rectangle _lastShown = Rectangle.Empty;
    private int _longSide;
    private nint _foregroundBeforeMenu;
    private bool _escapeClaimed;
    private Point _dragStart;
    private bool _pressed;
    private bool _controlAtPress;
    private bool _dragged;
    private bool _selecting;
    private bool _interacting;
    private Rectangle _selection;
    private bool _cleaned;
    private PipInteraction? _live;
    private readonly System.Collections.Concurrent.ConcurrentQueue<PressOutcome> _pressOutcomes = new();
    private nint _foregroundBeforeLive;
    private readonly ToolStripMenuItem _interactiveItem;


    [System.ComponentModel.DefaultValue(false)]
    public bool AllowClickForwarding { get; set; }

    /// <summary>Whether this view offers to move itself, which is an experimental effect.</summary>
    [System.ComponentModel.DefaultValue(false)]
    public bool AllowAutoMove { get; set; }

    [System.ComponentModel.DefaultValue(false)]
    public bool AllowInteractive { get; set; }

    [System.ComponentModel.DefaultValue(false)]
    public bool AllowSwitcherHiding { get; set; }

    public PipWindow(nint source, WindowController windows)
        : this(new WindowPipSource(WindowIdentity.Capture(source)), windows)
    {
    }

    internal PipWindow(PipSource source, WindowController windows)
    {
        _origin = source;
        _source = source.Window;
        _windows = windows;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black; // Whatever briefly shows while resizing shouldn't flash.
        SetStyle(ControlStyles.ResizeRedraw, true);
        StartPosition = FormStartPosition.Manual;
        Text = T("pip.titlePrefix") + source.Title;
        _longSide = Dpi.Scale(StartLongSide, source.Dpi);
        _tips.SetToolTip(this, source.IsWindow
            ? T("pip.controls.window")
            : T("pip.controls.screen"));

        _hideItem = new ToolStripMenuItem(T("pip.keepUpdating"), null,
            (_, _) => SetOriginalHidden(!_hideItem!.Checked));
        _switcherItem = new ToolStripMenuItem(T("pip.hideSwitcher"), null,
            (_, _) => SetSwitcherHidden(!_windows.IsOutOfSwitcher(_source)));
        _autoMoveItem = new ToolStripMenuItem(T("pip.autoMove"), null,
            (_, _) => SetAutoMove(!_autoMoveItem!.Checked));
        _interactiveItem = new ToolStripMenuItem(T("pip.interactive"), null,
            (_, _) => SetInteractive(!_interactiveItem!.Checked));
        var options = new ToolStripMenuItem(T("pip.options"));
        options.DropDownItems.Add(new ToolStripSeparator()); // Windows only draws the arrow with something inside.
        options.DropDownOpening += (_, _) => FillOptions(options);
        _menu.Items.Add(new ToolStripMenuItem { Name = "inputStatus", Enabled = false });
        _menu.Items.Add(options);
        _menu.Items.Add(_hideItem);
        _menu.Items.Add(_switcherItem);
        _menu.Items.Add(_autoMoveItem);
        _menu.Items.Add(_interactiveItem);
        _goToItem = _menu.Items.Add(T("pip.goToOriginal"), null, (_, _) => GoToSource());
        _removeCropItem = new ToolStripMenuItem(T("pip.removeCrop"), null, (_, _) => SetCrop(Rectangle.Empty));
        _menu.Items.Add(_removeCropItem);
        _menu.Items.Add(T("pip.diagnostics.title"), null, (_, _) => ShowDiagnostics());
        _retryItem = new ToolStripMenuItem(T("pip.backend.retry"), null, (_, _) => RetryCapture());
        _menu.Items.Add(_retryItem);
        _menu.Items.Add(T("pip.minimize"), null, (_, _) => SuspendView());
        _menu.Items.Add(T("pip.close"), null, (_, _) => Close());
        _menu.Opening += (_, _) =>
        {
            _menu.Items["inputStatus"]!.Text = InputDescription;
            _retryItem.Visible = CanRetryCapture;
            _autoMoveItem.Visible = AllowAutoMove;
            _interactiveItem.Visible = AllowInteractive && _origin.IsWindow;
            _hideItem.Visible = _goToItem.Visible = _origin.IsWindow;
            _switcherItem.Visible = AllowSwitcherHiding && _origin.IsWindow;
            _switcherItem.Enabled = _origin.IsAvailable && _windows.IsHidden(_source);
            _switcherItem.Checked = _origin.IsAvailable && _windows.IsOutOfSwitcher(_source);
            _removeCropItem.Enabled = !_crop.IsEmpty;
            // A menu only closes on an outside click while its program has the focus.
            _foregroundBeforeMenu = User32.GetForegroundWindow();
            User32.SetForegroundWindow(Handle);
        };
        _menu.Closed += (_, _) => ReturnFocusAfterMenu();
        ContextMenuStrip = _menu;

        _watch.Tick += (_, _) => FollowSource();
        _autoMove.Tick += (_, _) => MoveAwayFromContent();
    }

    /// <summary>Raised when Windows refuses something, with a message for the person.</summary>
    public event Action<string>? Problem;

    /// <summary>Raised for something the person should know that isn't a failure.</summary>
    public event Action<string>? Notice;

    /// <summary>The part of the source window being shown; empty means all of it.</summary>
    public Rectangle Crop => _crop;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE);
            return parameters;
        }
    }

    /// <summary>
    /// Opens the view with its picture already in place. It's created on top rather than pinned
    /// afterwards, because pinning a window needs the focus, which Windows can refuse and which belongs
    /// to the app in use.
    /// </summary>
    public void ShowOnTop()
    {
        _origin.Require();
        _sourceIcon ??= LoadSourceIcon();
        Icon = _sourceIcon;
        // A minimized window has no shape or picture to show, so opening a view needs it back, and says so.
        bool restored = User32.IsIconic(_source);
        if (restored)
            User32.ShowWindow(_source, User32.SW_SHOWNOACTIVATE);
        try
        {
            KeepOutOfScreenCapture();
            _thumbnail = new PipBackendSelector(_origin, Handle, BackendPreferences());
            _thumbnail.Problem += text => Problem?.Invoke(text);
            OpenInFreeSpot(_thumbnail);
            ApplyClickThrough();
            Show();
            StayOnTop();
            if ((User32.GetWindowLongPtr(Handle, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) == 0)
                WindowController.Pin(Handle);
            _watch.Start();
            if (restored)
                Notice?.Invoke(string.Format(T("pip.source.restoredMinimized"),
                    WindowInfo.Title(_source)));
        }
        catch
        {
            Close();
            throw;
        }
    }

    /// <summary>Takes new values from Settings. What this view was told to do differently stays.</summary>
    public void ApplyPipPreferences(PipPreferences preferences)
    {
        _global = preferences.Validated();
        Reconfigure();
    }

    public void ApplyViewPreferences(PipViewPreferences preferences)
    {
        _viewPreferences = preferences.Validated();
        _titleBar?.Dispose();
        _titleBar = null;
        _closeButton?.Dispose();
        _closeButton = null;
        DropHoverPreview();
    }

    internal bool IsSuspended => _suspended;
    internal bool SourceIsWindow => _origin.IsWindow;
    internal bool CanRetryCapture => _suspended || _thumbnail?.CanRetry == true;
    internal string InputDescription => _overrides.ClickThrough ? T("pip.state.clickThrough") :
        _interactiveItem.Checked ? T("pip.interactive") : AllowClickForwarding && _origin.IsWindow
            ? T("settings.pip.forwardClicks") : T("pip.input.activateOriginal");
    internal string StateDescription => string.Join(", ", new[]
    {
        _suspended ? T("pip.state.suspended") : _thumbnail?.StatusText ?? T("common.unavailable"),
        InputDescription,
        _hideItem.Checked ? T("pip.state.originalHidden") : "",
    }.Where(value => value.Length > 0));

    internal void RetryCapture()
    {
        if (_suspended) ResumeView();
        else _thumbnail?.Retry();
    }

    internal void SuspendView()
    {
        if (_suspended || _cleaned) return;
        CancelPress();
        EndLive();
        EndInteraction();
        CacheTaskbarPicture();
        _suspended = true;
        _resumeRequested = false;
        DropHoverPreview();
        _titleBar?.Hide();
        _closeButton?.Hide();
        _chip?.Hide();
        _autoMove.Stop();
        _thumbnail?.Dispose();
        _retiringCapture = _thumbnail?.Retirement ?? Task.CompletedTask;
        _thumbnail = null;
        Hide();
        if (_viewPreferences.MinimizeToTaskbar && _sourceIcon is not null)
            TaskbarProxy = new PipTaskbarProxy(_origin.Title, _sourceIcon, Bounds, TaskbarPreview, ResumeView, Close);
    }

    /// <summary>The taskbar button standing in for this view while it's minimized there.</summary>
    internal PipTaskbarProxy? TaskbarProxy { get; private set; }

    private void DropTaskbarProxy()
    {
        TaskbarProxy?.Dismiss();
        TaskbarProxy = null;
    }

    /// <summary>
    /// A still picture of what the view shows, made only when Windows asks. Views of a display or
    /// region have no window to ask, so they show their icon.
    /// </summary>
    private void CacheTaskbarPicture()
    {
        if (!Visible || Width <= 0 || Height <= 0) return;
        try
        {
            var picture = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                using Graphics graphics = Graphics.FromImage(picture);
                graphics.CopyFromScreen(Location, Point.Empty, Size);
            }
            catch { picture.Dispose(); throw; }
            _taskbarPicture?.Dispose();
            _taskbarPicture = picture;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or ExternalException or ArgumentException)
        {
            // The taskbar can use the source icon while the desktop is locked.
        }
    }

    private Bitmap? TaskbarPreview(Size limit)
    {
        if (_taskbarPicture is null) return null;
        return new Bitmap(_taskbarPicture, PipGeometry.FitInside(_taskbarPicture.Size, limit));
    }

    internal void ResumeView()
    {
        if (!_suspended || _cleaned) return;
        if (!_origin.IsAvailable)
        {
            if (_origin.IsWindow) Close();
            else Problem?.Invoke(T("errors.pip.displayUnavailable"));
            return;
        }
        if (!_retiringCapture.IsCompleted) { _resumeRequested = true; return; }
        _resumeRequested = false;
        try
        {
            _thumbnail = new PipBackendSelector(_origin, Handle, BackendPreferences());
            _thumbnail.Problem += text => Problem?.Invoke(text);
            _thumbnail.SetViewport(PipGeometry.ViewArea(ClientSize), _lastShown);
            _suspended = false;
            DropTaskbarProxy();
            ApplyClickThrough();
            Show();
            StayOnTop();
            if (_autoMoveItem.Checked) _autoMove.Start();
        }
        catch (WindowOperationException failure)
        {
            _thumbnail?.Dispose();
            _thumbnail = null;
            _suspended = true;
            Problem?.Invoke(failure.Message);
        }
    }

    private Icon LoadSourceIcon()
    {
        try
        {
            string path = _origin.IsWindow ? WindowInfo.ExecutablePath(_source) : "";
            if (!string.IsNullOrEmpty(path) && Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or ExternalException or System.ComponentModel.Win32Exception) { }
        return (Icon)SystemIcons.Application.Clone();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal PipOverrides Overrides
    {
        get => _overrides;
        set
        {
            _overrides = value;
            Reconfigure();
        }
    }

    internal PipPreferences EffectivePreferences => _preferences;

    /// <summary>The saved presets to offer, and where a new or removed one goes. Set by the owner of the settings.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IReadOnlyList<PipPreset>>? Presets { get; set; }
    public event Action<PipPreset>? PresetSaved;
    public event Action<string>? PresetRemoved;

    private void Reconfigure()
    {
        _preferences = _global.With(_overrides);
        // A view clicks can pass through is an overlay, so its opacity is real see-through, not a fade to black.
        _thumbnail?.Configure(BackendPreferences());
        ApplyClickThrough();
        // Switching to smooth capture on Windows 10 brings a hidden switcher entry back, or the view would stop.
        if (_thumbnail is not null && _origin.IsWindow && SwitcherHidingStopsView && _windows.IsOutOfSwitcher(_source))
        {
            SetSwitcherHidden(false);
            Problem?.Invoke(T("errors.switcher.windows10"));
        }
    }

    private PipPreferences BackendPreferences() => _overrides.ClickThrough
        ? _preferences.With(new PipOverrides(OpacityPercent: 100)) : _preferences;

    /// <summary>Click-through needs the layered style too, or Windows still gives this window the clicks.</summary>
    private void ApplyClickThrough()
    {
        if (!IsHandleCreated)
            return;
        const long both = User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT;
        long style = User32.GetWindowLongPtr(Handle, User32.GWL_EXSTYLE);
        long wanted = _overrides.ClickThrough ? style | both : style & ~both;
        if (wanted != style)
            User32.SetWindowLongPtr(Handle, User32.GWL_EXSTYLE, (nint)wanted);
        if (_overrides.ClickThrough)
            User32.SetLayeredWindowAttributes(Handle, 0, _preferences.Alpha, User32.LWA_ALPHA);
    }

    private void FillOptions(ToolStripMenuItem options)
    {
        options.DropDownItems.Clear();
        options.DropDownItems.Add(new ToolStripMenuItem(
            _overrides.FrameRate is null && _overrides.Scaling is null && _overrides.OpacityPercent is null
                ? T("pip.defaults.inherited") : T("pip.defaults.overridden")) { Enabled = false });
        options.DropDownItems.Add(new ToolStripMenuItem(T("pip.controls.help")) { Enabled = false });
        options.DropDownItems.Add(T("pip.zoom.reset"), null, (_, _) => { ResetZoom(); SetCrop(Rectangle.Empty); });
        bool capture = BackendKind != PipBackendKind.DwmThumbnail;
        ToolStripMenuItem rate = Choices(T("pip.frameRate"), _overrides.FrameRate,
            [(T("pip.useSettings"), null), (T("pip.frameRate.matchMonitor"), 0), ("15 FPS", 15), ("30 FPS", 30), ("60 FPS", 60), ("120 FPS", 120)],
            value => Overrides = _overrides with { FrameRate = value });
        ToolStripMenuItem scaling = Choices(T("pip.scaling"), _overrides.Scaling,
            [(T("pip.useSettings"), null), (T("pip.scaling.auto"), PipScaling.Auto), (T("pip.scaling.crisp"), PipScaling.Crisp),
                (T("pip.scaling.smooth"), PipScaling.Smooth)],
            value => Overrides = _overrides with { Scaling = value });
        rate.Enabled = scaling.Enabled = capture;
        options.DropDownItems.Add(rate);
        options.DropDownItems.Add(scaling);
        options.DropDownItems.Add(Choices(T("pip.opacity"), _overrides.OpacityPercent,
            [(T("pip.useSettings"), null), ("100%", 100), ("75%", 75), ("50%", 50), ("25%", 25)],
            value => Overrides = _overrides with { OpacityPercent = value }));
        options.DropDownItems.Add(new ToolStripMenuItem(T("pip.lockPosition"), null,
            (_, _) => Overrides = _overrides with { PositionLocked = !_overrides.PositionLocked })
            { Checked = _overrides.PositionLocked });
        options.DropDownItems.Add(new ToolStripMenuItem(T("pip.clickThrough"), null,
            (_, _) => Overrides = _overrides with { ClickThrough = true }));
        options.DropDownItems.Add(new ToolStripSeparator());
        var size = new ToolStripMenuItem(T("pip.size"));
        size.DropDownItems.Add(T("pip.size.small"), null, (_, _) => ResizeTo(Dpi.Scale(SmallLongSide, DeviceDpi)));
        size.DropDownItems.Add(T("pip.size.medium"), null, (_, _) => ResizeTo(Dpi.Scale(MediumLongSide, DeviceDpi)));
        size.DropDownItems.Add(T("pip.size.large"), null, (_, _) => ResizeTo(Dpi.Scale(LargeLongSide, DeviceDpi)));
        size.DropDownItems.Add(T("pip.size.fitSource"), null, (_, _) => FitToSourceSize());
        options.DropDownItems.Add(size);
        var presets = new ToolStripMenuItem(T("pip.presets"));
        IReadOnlyList<PipPreset> saved = Presets?.Invoke() ?? [];
        foreach (PipPreset preset in saved)
        {
            var entry = new ToolStripMenuItem(preset.Name.Replace("&", "&&"));
            entry.DropDownItems.Add(new ToolStripMenuItem(PresetChanges(preset)) { Enabled = false });
            entry.DropDownItems.Add(T("common.apply"), null, (_, _) => ApplyPreset(preset));
            presets.DropDownItems.Add(entry);
        }
        if (saved.Count > 0)
            presets.DropDownItems.Add(new ToolStripSeparator());
        presets.DropDownItems.Add(T("pip.presets.save"), null, (_, _) => SavePreset());
        if (saved.Count > 0)
        {
            var remove = new ToolStripMenuItem(T("pip.presets.remove"));
            foreach (PipPreset preset in saved)
                remove.DropDownItems.Add(preset.Name.Replace("&", "&&"), null, (_, _) => PresetRemoved?.Invoke(preset.Name));
            presets.DropDownItems.Add(remove);
        }

        options.DropDownItems.Add(presets);
    }

    private string PresetChanges(PipPreset preset)
    {
        PipPreset current = CurrentPreset(preset.Name);
        var changes = new List<string>();
        if (preset.CropFor(WholeSize()) != Crop || _zoom != PipGeometry.WholeView) changes.Add(T("window.crop"));
        if (preset.LongSide > 0 && preset.LongSide != current.LongSide) changes.Add(T("pip.size") + $": {preset.LongSide} px");
        if (preset.Device is not null && (preset.Device != current.Device || preset.Left != current.Left || preset.Top != current.Top))
            changes.Add(T("pip.presets.position"));
        if (preset.FrameRate != current.FrameRate) changes.Add(T("pip.frameRate") + ": " + (preset.FrameRate?.ToString() ?? T("pip.useSettings")));
        if (preset.OpacityPercent != current.OpacityPercent) changes.Add(T("pip.opacity") + ": " + (preset.OpacityPercent?.ToString() ?? T("pip.useSettings")));
        if (preset.Scaling != current.Scaling) changes.Add(T("pip.scaling"));
        if (preset.PositionLocked is { } locked && locked != current.PositionLocked) changes.Add(T("pip.lockPosition") + ": " + (locked ? T("common.yes") : T("common.no")));
        return changes.Count == 0 ? T("pip.presets.noChanges") : string.Join("; ", changes);
    }

    private static ToolStripMenuItem Choices<T1>(string title, T1 current, (string Label, T1 Value)[] choices, Action<T1> choose)
    {
        var menu = new ToolStripMenuItem(title);
        foreach ((string label, T1 value) in choices)
            menu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => choose(value))
                { Checked = EqualityComparer<T1>.Default.Equals(value, current) });
        return menu;
    }

    private void ResizeTo(int longSide)
    {
        _longSide = longSide;
        Attempt(() => FitToSource());
    }

    /// <summary>One view pixel for each source pixel, as far as the screen allows.</summary>
    private void FitToSourceSize()
    {
        if (_thumbnail is null) return;
        Rectangle part = CroppedPart(_thumbnail.SourceSize);
        ResizeTo(Math.Max(part.Width, part.Height) + 2 * PipGeometry.Border);
    }

    internal PipPreset CurrentPreset(string name)
    {
        (double left, double top) = PipGeometry.ToShares(Bounds, Screen.FromControl(this).WorkingArea);
        Size whole = WholeSize();
        return new PipPreset
        {
            Name = name, CropX = _crop.X, CropY = _crop.Y, CropWidth = _crop.Width, CropHeight = _crop.Height,
            LongSide = _longSide, Device = Screen.FromControl(this).DeviceName, Left = left, Top = top,
            FrameRate = _overrides.FrameRate, OpacityPercent = _overrides.OpacityPercent, Scaling = _overrides.Scaling,
            CropShares = PipCropShares.Of(_crop, whole), PositionLocked = _overrides.PositionLocked,
        };
    }

    /// <summary>The size a crop is measured against, or empty before the view has a picture.</summary>
    private Size WholeSize() => _thumbnail is null ? Size.Empty : _origin.Whole(_thumbnail.SourceSize).Size;

    /// <summary>Whether the original is hidden while this view keeps showing it.</summary>
    internal bool KeepsUpdating => _hideItem.Checked;

    internal void ApplyPreset(PipPreset preset)
    {
        Overrides = _overrides with
        {
            FrameRate = preset.FrameRate, OpacityPercent = preset.OpacityPercent, Scaling = preset.Scaling,
            PositionLocked = preset.PositionLocked ?? _overrides.PositionLocked,
        };
        _crop = preset.CropFor(WholeSize());
        _zoom = PipGeometry.WholeView;
        if (preset.LongSide > 0)
            _longSide = preset.LongSide;
        Attempt(() => FitToSource());
        if (preset.Device is null)
            return;
        Screen screen = Screen.AllScreens.FirstOrDefault(each => each.DeviceName == preset.Device) ?? Screen.FromControl(this);
        Location = PipGeometry.KeepInside(
            new Rectangle(PipGeometry.FromShares(preset.Left, preset.Top, Size, screen.WorkingArea), Size), screen.WorkingArea);
    }

    private void SavePreset()
    {
        using var dialog = new Form
        {
            Text = T("pip.presets.save"), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, TopMost = true, ClientSize = new Size(320, 72), ShowInTaskbar = false,
        };
        var name = new TextBox { Left = 10, Top = 10, Width = 300, MaxLength = PipPreset.NameLimit };
        var save = new Button { Text = T("common.save"), Left = 235, Top = 40, Width = 75, DialogResult = DialogResult.OK };
        dialog.Controls.AddRange([name, save]);
        dialog.AcceptButton = save;
        if (dialog.ShowDialog() == DialogResult.OK && name.Text.Trim().Length > 0)
            PresetSaved?.Invoke(CurrentPreset(name.Text.Trim()));
    }

    internal PipBackendKind BackendKind => _thumbnail?.Kind ?? PipBackendKind.DwmThumbnail;
    internal PipSessionState BackendState => _thumbnail?.State ?? PipSessionState.Stopped;
    internal string DiagnosticSnapshot => _thumbnail?.Snapshot() ?? "Stopped";

    private void ShowDiagnostics()
    {
        var dialog = new Form { Text = T("pip.diagnostics.title"), Size = new Size(700, 380), StartPosition = FormStartPosition.CenterParent };
        var text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, Text = DiagnosticSnapshot.Replace("\n", Environment.NewLine) };
        var copy = new Button { Text = T("pip.diagnostics.copy"), Dock = DockStyle.Bottom };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text.Text); }
            catch (ExternalException) { Problem?.Invoke(T("errors.clipboard.busy")); }
        };
        dialog.Controls.Add(text); dialog.Controls.Add(copy);
        using (dialog) dialog.ShowDialog();
    }

    /// <summary>
    /// A view of the screen would otherwise show itself, and then show that, without end. Windows only
    /// honours this from version 2004; before that the view would appear as a black box instead.
    /// </summary>
    private void KeepOutOfScreenCapture()
    {
        if (_origin.IsWindow)
            return;
        if (Environment.OSVersion.Version.Build < MinimumExclusionBuild ||
            !User32.SetWindowDisplayAffinity(Handle, User32.WDA_EXCLUDEFROMCAPTURE))
            throw new WindowOperationException(T("errors.pip.screenUnsupported"));
    }

    private void KeepPartOutOfScreenCapture(Form part)
    {
        if ((!_origin.IsWindow || _autoMoveItem.Checked) && !User32.SetWindowDisplayAffinity(part.Handle, User32.WDA_EXCLUDEFROMCAPTURE))
            throw new WindowOperationException(T("errors.pip.screenUnsupported"));
    }

    /// <summary>Crops the view to a part of the source window, or shows all of it when empty.</summary>
    /// <param name="crop">The part to show, measured from the window's visible top-left corner.</param>
    public void SetCrop(Rectangle crop)
    {
        if (crop != _crop)
            _zoom = PipGeometry.WholeView;
        _crop = crop;
        Attempt(() => FitToSource());
    }

    /// <summary>
    /// Turns moving away from text and images on or off. Telling what is under the view needs a look
    /// at the screen without the view on it, so while this is on the view is left out of anything that
    /// captures the screen, screenshots and screen sharing included.
    /// </summary>
    public void SetAutoMove(bool on)
    {
        if (!User32.SetWindowDisplayAffinity(Handle, on || !_origin.IsWindow ? User32.WDA_EXCLUDEFROMCAPTURE : User32.WDA_NONE))
        {
            Problem?.Invoke(T("errors.pip.autoMoveUnsupported"));
            return;
        }

        _autoMoveItem.Checked = on;
        _autoMove.Enabled = on && !_suspended;
        foreach (Form part in OwnedForms)
            User32.SetWindowDisplayAffinity(part.Handle, on || !_origin.IsWindow ? User32.WDA_EXCLUDEFROMCAPTURE : User32.WDA_NONE);
    }

    /// <summary>Makes the original invisible, or brings it back. The view keeps updating either way.</summary>
    public void SetOriginalHidden(bool hidden)
    {
        if (!_origin.IsWindow)
            return;
        if (!hidden) { EndLive(); EndInteraction(); }
        try
        {
            _origin.Require();
            if (hidden && _thumbnail is null)
                throw new WindowOperationException(
                    T("errors.pip.previewNotReady"));
            if (hidden)
            {
                _windows.HideOwned(_source, this);
                if (User32.GetForegroundWindow() == _source)
                    User32.SetForegroundWindow(Handle); // Keys mustn't go to a window that can't be seen.
                StayOnTop();
            }
            else
            {
                _windows.ShowOwned(_source, this);
            }

            _hideItem.Checked = hidden;
        }
        catch (WindowOperationException failure)
        {
            if (hidden && _origin.IsAvailable && _windows.IsHidden(_source))
            {
                try
                {
                    _windows.ShowOwned(_source, this);
                }
                catch (WindowOperationException)
                {
                }
            }

            _hideItem.Checked = _origin.IsAvailable && _windows.IsConcealedBy(_source, this);
            Problem?.Invoke(failure.Message);
        }
    }

    /// <summary>
    /// Windows 10 can't capture a window once it is out of Alt+Tab and the taskbar, so a view that may use
    /// smooth capture would stop updating. Windows 11 can.
    /// </summary>
    private bool SwitcherHidingStopsView =>
        Environment.OSVersion.Version.Build < FirstBuildCapturingSwitcherHiddenWindows && _preferences.Backend != PipBackendPreference.Dwm;

    private const int FirstBuildCapturingSwitcherHiddenWindows = 22000;

    public void SetSwitcherHidden(bool hidden)
    {
        if (!_origin.IsWindow || hidden && !AllowSwitcherHiding) return;
        if (hidden && SwitcherHidingStopsView)
        {
            Problem?.Invoke(T("errors.switcher.windows10"));
            return;
        }
        try
        {
            _origin.Require();
            _windows.SetOutOfSwitcher(_source, hidden);
            _switcherItem.Checked = hidden;
        }
        catch (WindowOperationException failure) { Problem?.Invoke(failure.Message); }
    }

    protected override void WndProc(ref Message m)
    {
        // Keep the modifiers carried by the press. This inactive view may process it after Ctrl is released.
        if (m.Msg is 0x0201 or 0x0203)
            _controlAtPress = (m.WParam.ToInt64() & 0x0008) != 0; // MK_CONTROL
        if (m.Msg == WM_DISPLAYCHANGE || (m.Msg == 0x0218 && (m.WParam == 7 || m.WParam == 18)))
        {
            PipDisplay.InvalidateRefresh();
            _thumbnail?.DisplayChanged();
        }
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }

        if (m.Msg == User32.WM_HOTKEY && m.WParam == EscapeHotkey)
        {
            CancelPress();
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Accent, PipGeometry.Border) { Alignment = PenAlignment.Inset };
        e.Graphics.DrawRectangle(border, new Rectangle(Point.Empty, ClientSize));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _longSide = _longSide * e.DeviceDpiNew / Math.Max(1, e.DeviceDpiOld);
        Attempt(() => FitToSource());
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_pressed || _lastShown.IsEmpty)
            return;

        if ((ModifierKeys & Keys.Control) != 0)
        {
            Rectangle shown = PipGeometry.ViewArea(ClientSize);
            ZoomAt(new PointF((e.X - shown.Left) / (float)shown.Width, (e.Y - shown.Top) / (float)shown.Height),
                Math.Pow(ZoomStep, e.Delta / (double)WheelNotch));
            return;
        }

        // Touchpads send many tiny steps, so the size follows how far the wheel really moved.
        int step = (int)Math.Round(Dpi.Scale(WheelStep, DeviceDpi) * (e.Delta / (double)WheelNotch));
        if (step == 0)
            step = Math.Sign(e.Delta);

        Rectangle area = Screen.FromControl(this).WorkingArea;
        int longSide = Math.Clamp(_longSide + step, Dpi.Scale(PipGeometry.MinimumWidth, DeviceDpi),
            Math.Max(area.Width, area.Height));
        Size size = PipGeometry.FitLongSide(longSide, _lastShown, area.Size);
        if (step < 0 && Math.Min(size.Width, size.Height) < Dpi.Scale(PipGeometry.MinimumHeight, DeviceDpi))
            return; // Any smaller and there would be nothing left to grab.

        _longSide = Math.Max(size.Width, size.Height);
        Point? pointer = _overrides.PositionLocked ? null : PointToScreen(e.Location);
        Attempt(() => FitToSource(pointer));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Middle && _zoom != PipGeometry.WholeView)
        {
            _panning = true;
            _panFrom = e.Location;
        }

        if (e.Button != MouseButtons.Left)
            return;

        _dragStart = e.Location;
        _pressed = true;
        _dragged = false;
        if (_controlAtPress && _thumbnail is not null)
        {
            IPipRenderBackend view = _thumbnail;
            Attempt(() => BeginSelection(view));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_interactiveItem.Checked && e.Button == MouseButtons.None && !_pressed)
            BeginLive(e.Location);
        if (_panning && e.Button == MouseButtons.Middle)
        {
            Rectangle area = PipGeometry.ViewArea(ClientSize);
            _zoom = PipGeometry.Pan(_zoom, (_panFrom.X - e.X) * _zoom.Width / area.Width,
                (_panFrom.Y - e.Y) * _zoom.Height / area.Height);
            _panFrom = e.Location;
            Attempt(() => FitToSource());
            return;
        }

        if (e.Button != MouseButtons.Left || !_pressed)
            return;

        if (_selecting)
        {
            _selection = Rectangle.Intersect(PipGeometry.Between(_dragStart, e.Location),
                PipGeometry.ViewArea(ClientSize));
            Attempt(ShowSelection);
        }
        else if (_dragged || Math.Abs(e.X - _dragStart.X) >= DragThreshold ||
                 Math.Abs(e.Y - _dragStart.Y) >= DragThreshold)
        {
            _dragged = true;
            if (_overrides.PositionLocked)
                return;
            Location = PipGeometry.KeepInside(
                new Rectangle(new Point(Left + e.X - _dragStart.X, Top + e.Y - _dragStart.Y), Size),
                Screen.FromPoint(Cursor.Position).WorkingArea);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle)
            _panning = false;
        if (e.Button != MouseButtons.Left || !_pressed)
            return;

        _pressed = false;
        if (_selecting)
        {
            EndSelection();
            if (_selection.Width < 10 || _selection.Height < 10 || _thumbnail is null)
            {
                SetCrop(_crop);
            }
            else
            {
                Rectangle part = PipGeometry.SelectionToSource(_selection, PipGeometry.ViewArea(ClientSize),
                    ShownPart(_thumbnail.SourceSize));
                part.Offset(-_whole.X, -_whole.Y);
                SetCrop(part);
            }
        }
        else if (!_dragged && !_controlAtPress)
        {
            Interact(e.Location);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || !_controlAtPress)
            return;
        if (!ResetZoom())
            SetCrop(Rectangle.Empty);
    }

    /// <param name="spot">The point to hold still, as a share of the view.</param>
    internal void ZoomAt(PointF spot, double factor)
    {
        _zoom = PipGeometry.ZoomAround(_zoom, spot, factor);
        Attempt(() => FitToSource());
    }

    /// <returns>False when the view wasn't zoomed.</returns>
    internal bool ResetZoom()
    {
        if (_zoom == PipGeometry.WholeView)
            return false;
        _zoom = PipGeometry.WholeView;
        Attempt(() => FitToSource());
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        EndLive();
        EndInteraction();
        // Keep the recovery surface reachable if a concealed source is temporarily unresponsive.
        if (_origin.IsWindow && _origin.IsAvailable &&
            (_windows.IsHidden(_source) || _windows.IsOutOfSwitcher(_source)))
        {
            SetOriginalHidden(false);
            if (_windows.IsHidden(_source) || _windows.IsOutOfSwitcher(_source)) e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Cleanup();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Cleanup();
        base.Dispose(disposing);
    }

    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        DropTaskbarProxy();
        _watch.Dispose();
        _autoMove.Dispose();
        ClaimEscape(false);
        EndLive();
        // Teardown must never submit another draw: the source or destination HWND may already be gone.
        _selecting = false;
        _thumbnail?.Dispose();
        _thumbnail = null;
        EndInteraction();
        if (_hideItem.Checked && _origin.IsAvailable)
            SetOriginalHidden(false);
        _tips.Dispose();
        DropHoverPreview();
        _titleBar?.Dispose();
        _sourceIcon?.Dispose();
        _taskbarPicture?.Dispose();
        _closeButton?.Dispose();
        _chip?.Dispose();
        _menu.Dispose();
    }

    /// <summary>
    /// Puts the real window under the cursor with the clicked spot exactly beneath it, then passes the
    /// click on. Windows and apps see ordinary input, so this works in any program, keyboard included.
    /// The window goes back when the cursor leaves it.
    /// </summary>
    private void Interact(Point point)
    {
        if (_thumbnail is null || _interacting || !_origin.IsWindow)
            return;
        if (!AllowClickForwarding)
        {
            GoToSource();
            return;
        }

        Rectangle view = PipGeometry.ViewArea(ClientSize);
        if (!view.Contains(point))
            return;

        Size sourceSize = _thumbnail.SourceSize;
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            GoToSource();
            return;
        }

        Point spot = PipGeometry.PointToSource(point, view, ShownPart(sourceSize));
        Point cursor = PointToScreen(point);
        Point position = PipGeometry.PeekPosition(cursor, spot, sourceSize, Screen.FromPoint(cursor).WorkingArea);

        try
        {
            _origin.Require();
            if (_windows.IsClickThrough(_source))
            {
                GoToSource();
                return;
            }

            position = _windows.BeginPeek(_source, position);
            _interacting = true;
            var target = new Point(position.X + spot.X, position.Y + spot.Y);
            // Activation and hit-test must agree after repositioning; a transparent, covered, cloaked,
            // replaced, or refused source must never turn into a desktop click.
            Cursor.Position = target;
            if (!_origin.IsAvailable || User32.GetForegroundWindow() != _source || !WindowInfo.IsAvailable(_source) ||
                (User32.GetWindowLongPtr(_source, User32.GWL_EXSTYLE) & User32.WS_EX_TRANSPARENT) != 0 ||
                User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = target.X, Y = target.Y }),
                    User32.GA_ROOT) != _source)
                throw new WindowOperationException(
                    T(
                        "errors.pip.clickUnverified"));
            User32.ClickLeftButton();
        }
        catch (WindowOperationException failure)
        {
            EndInteraction();
            Cursor.Position = cursor;
            Problem?.Invoke(failure.Message);
            GoToSource();
            return;
        }
    }

    /// <summary>Turns direct use of this view on or off. It needs the original hidden: sliding a visible window around isn't acceptable.</summary>
    internal void SetInteractive(bool on)
    {
        if (on && (!AllowInteractive || !_origin.IsWindow)) return;
        if (on)
        {
            if (!PressRules.SharedScaling(Screen.AllScreens.Select(ScreenDpi)))
            {
                Notice?.Invoke(T("pip.interactive.mixedDpi"));
                return;
            }

            SetOriginalHidden(true);
            if (!_windows.IsHidden(_source))
                return;
            if (_overrides.ClickThrough)
                Overrides = _overrides with { ClickThrough = false };
        }
        else
        {
            EndLive();
        }

        _interactiveItem.Checked = on;
    }

    private static int ScreenDpi(Screen screen)
    {
        return Dpi.ScaleOf(User32.MonitorFromPoint(new NativePoint { X = screen.Bounds.Left, Y = screen.Bounds.Top }, 2));
    }

    internal PipInteraction? LiveInteraction => _live;

    /// <summary>Every press's outcome over this view's life, so none can pass unnoticed in tests.</summary>
    internal IReadOnlyCollection<PressOutcome> PressOutcomes => _pressOutcomes;

    /// <summary>The pointer came onto the view. Everything that lets real input through exists from here until it leaves.</summary>
    private void BeginLive(Point pointer)
    {
        if (_live is not null || _thumbnail is null || _suspended || !_origin.IsAvailable || !_windows.IsHidden(_source))
            return;
        Rectangle area = PipGeometry.ViewArea(ClientSize);
        if (!area.Contains(pointer))
            return;
        _foregroundBeforeLive = User32.GetForegroundWindow();
        var live = new PipInteraction(_source, RectangleToScreen(area), ShownPart(_thumbnail.SourceSize));
        live.Left += () => BeginInvokeIfAlive(EndLive);
        live.Pressed += _pressOutcomes.Enqueue;
        live.ViewDragged += moved => BeginInvokeIfAlive(() => DragLiveView(moved));
        try
        {
            _windows.BeginInteractive(_source, Handle);
            SetInputPassesThrough(true);
            live.Start(PointToScreen(pointer));
            _live = live;
        }
        catch (WindowOperationException failure)
        {
            live.Dispose();
            SetInputPassesThrough(false);
            try { _windows.EndInteractive(_source); }
            catch (WindowOperationException) { }
            _interactiveItem.Checked = false;
            Problem?.Invoke(failure.Message);
        }
    }

    private void BeginInvokeIfAlive(Action action)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(action);
    }

    private void DragLiveView(Size moved)
    {
        if (_live is null || _overrides.PositionLocked)
            return;
        Location += moved;
        if (_thumbnail is not null)
            _live.Update(RectangleToScreen(PipGeometry.ViewArea(ClientSize)), ShownPart(_thumbnail.SourceSize));
    }

    /// <summary>In this order: the source stops taking clicks, goes home, the hook goes, the view takes input, keys come back.</summary>
    private void EndLive()
    {
        if (_live is not { } live)
            return;
        _live = null;
        live.Dispose(); // Stop the mover before returning the source to its journaled placement.
        try
        {
            if (_origin.IsAvailable)
                _windows.EndInteractive(_source);
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
        }

        SetInputPassesThrough(false);
        if (User32.GetForegroundWindow() == _source)
        {
            nint back = _foregroundBeforeLive != 0 && _foregroundBeforeLive != _source ? _foregroundBeforeLive : Handle;
            User32.SetForegroundWindow(back); // Keys mustn't go to a window that can't be seen.
        }

        if (!IsDisposed)
            StayOnTop();
    }

    private void SetInputPassesThrough(bool on)
    {
        if (!IsHandleCreated)
            return;
        if (!on)
        {
            ApplyClickThrough();
            return;
        }

        const long both = User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT;
        User32.SetWindowLongPtr(Handle, User32.GWL_EXSTYLE, (nint)(User32.GetWindowLongPtr(Handle, User32.GWL_EXSTYLE) | both));
        User32.SetLayeredWindowAttributes(Handle, 0, _preferences.Alpha, User32.LWA_ALPHA);
    }

    private void EndInteraction()
    {
        if (!_interacting)
            return;

        _interacting = false;
        try
        {
            _windows.EndPeek(_source);
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
        }
    }

    /// <summary>
    /// Dims the picture while keeping the selected region bright. DWM uses a second thumbnail;
    /// native capture renders the corresponding mask over its cached source texture.
    /// </summary>
    private void BeginSelection(IPipRenderBackend view)
    {
        _selecting = true;
        _selection = Rectangle.Empty;
        BackColor = Color.Black;
        view.SetSelection(Rectangle.Empty);
    }

    private void ShowSelection() => _thumbnail?.SetSelection(_selection);

    private void EndSelection()
    {
        _selecting = false;
        Attempt(() => _thumbnail?.SetSelection(null));
    }

    /// <summary>Drops a press whose release never arrived, or that Esc called off, and undoes its effects.</summary>
    private void CancelPress()
    {
        _pressed = false;
        _dragged = false;
        _panning = false;
        if (!_selecting)
            return;
        EndSelection();
        Attempt(() => FitToSource());
    }

    /// <summary>
    /// The view doesn't take the focus, so Esc can't reach it the usual way. It's claimed only during a
    /// crop drag: a pointer merely resting on the view mustn't take Esc from a dialog or a game.
    /// </summary>
    private void ClaimEscape(bool claim)
    {
        if (claim == _escapeClaimed || !IsHandleCreated)
            return;
        _escapeClaimed = claim;
        if (claim)
            User32.RegisterHotKey(Handle, EscapeHotkey, User32.MOD_NOREPEAT, (uint)Keys.Escape);
        else
            User32.UnregisterHotKey(Handle, EscapeHotkey);
    }

    private void ReturnFocusAfterMenu()
    {
        if (!IsHandleCreated || IsDisposed || User32.GetForegroundWindow() != Handle)
            return;
        bool hiddenSource = _foregroundBeforeMenu == _source && _hideItem.Checked;
        if (_foregroundBeforeMenu != 0 && !hiddenSource)
            User32.SetForegroundWindow(_foregroundBeforeMenu);
    }

    /// <summary>Puts this above other topmost windows, which a hidden original just went above too.</summary>
    private void StayOnTop()
    {
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    /// <summary>A view that couldn't update says so and stays open; the next change can put it right.</summary>
    private void Attempt(Action action)
    {
        try
        {
            action();
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
        }
    }

    /// <summary>
    /// The part of the source on show, in preview coordinates. A minimized window has nothing to measure,
    /// so it keeps its last shape. The crop itself is left alone, so it comes back whole when a source
    /// that shrank past it grows again.
    /// </summary>
    private Rectangle ShownPart(Size sourceSize) => PipGeometry.ZoomedPart(CroppedPart(sourceSize), _zoom);

    /// <summary>The part the view stands for before any zoom, which is what sets the view's shape.</summary>
    private Rectangle CroppedPart(Size sourceSize)
    {
        if (_whole.IsEmpty || !_origin.IsMinimized)
            _whole = _origin.Whole(sourceSize);
        if (_crop.IsEmpty)
            return _whole;

        var cropped = new Rectangle(_whole.X + _crop.X, _whole.Y + _crop.Y, _crop.Width, _crop.Height);
        cropped.Intersect(_whole);
        return cropped.Width > 0 && cropped.Height > 0 ? cropped : _whole;
    }

    private void OpenInFreeSpot(IPipRenderBackend view)
    {
        Rectangle shown = ShownPart(view.SourceSize);
        if (shown.Width <= 0 || shown.Height <= 0)
            throw new WindowOperationException(T("errors.pip.noSurface"));

        Rectangle area = _origin.Screen.WorkingArea;
        int margin = Dpi.Scale(ScreenMargin, _origin.Dpi);
        Size size = PipGeometry.FitLongSide(_longSide, shown, area.Size);
        var wanted = new Rectangle(area.Right - size.Width - margin, area.Bottom - size.Height - margin,
            size.Width, size.Height);
        var others = Application.OpenForms.OfType<PipWindow>().Where(other => other != this && other.Visible)
            .Select(other => other.Bounds);
        Bounds = new Rectangle(PipGeometry.FreeSpot(wanted, others, area, margin), size);
        _lastShown = shown;
        view.SetViewport(PipGeometry.ViewArea(ClientSize), shown);
    }

    /// <param name="pointer">Keeps this screen point on the same spot of the view; otherwise it holds its corner.</param>
    private void FitToSource(Point? pointer = null)
    {
        if (_thumbnail is null)
            return;

        Rectangle shown = ShownPart(_thumbnail.SourceSize);
        if (shown.Width <= 0 || shown.Height <= 0)
            throw new WindowOperationException(T("errors.pip.noSurface"));

        Rectangle area = Screen.FromControl(this).WorkingArea;
        Size size = PipGeometry.FitLongSide(_longSide, CroppedPart(_thumbnail.SourceSize), area.Size);
        Point place = pointer is { } spot
            ? PipGeometry.KeepUnderPointer(Bounds, size, spot)
            : PipGeometry.KeepCorner(Bounds, size, area);
        Bounds = new Rectangle(PipGeometry.KeepInside(new Rectangle(place, size), area), size);
        _lastShown = shown;
        _thumbnail.SetViewport(PipGeometry.ViewArea(ClientSize), shown);
    }

    private void FollowSource()
    {
        if (!_origin.IsAvailable)
        {
            if (_origin.IsWindow) Close();
            else if (!_suspended) SuspendView();
            return;
        }
        if (_resumeRequested && _retiringCapture.IsCompleted) ResumeView();

        string title = T("pip.titlePrefix") + _origin.Title;
        if (Text != title) Text = title;
        if (_suspended) return;

        try
        {
            _thumbnail?.Poll(ClientSize);
        }
        catch (WindowOperationException failure)
        {
            // If even the independent compatibility surface fails, close through normal restoration.
            // Repeating the same draw from this timer would spam the tray and strand a hidden source.
            Problem?.Invoke(failure.Message);
            if (_origin.IsWindow) Close();
            else SuspendView();
            return;
        }
        if (_hideItem.Checked && !_windows.IsHidden(_source))
            _hideItem.Checked = false;
        if (_pressed && MouseButtons == MouseButtons.None)
            CancelPress();
        try { FollowPointer(); ShowState(); }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
            Close();
            return;
        }

        if (_live is not null && _thumbnail is not null)
        {
            _live.Update(RectangleToScreen(PipGeometry.ViewArea(ClientSize)), ShownPart(_thumbnail.SourceSize));
            // Windows drops a hook it thinks is slow. The source must not stay clickable if that happens.
            if (!Bounds.Contains(Cursor.Position) && MouseButtons == MouseButtons.None && !_live.InPopup)
                EndLive();
        }

        if (_interacting && ShouldEndInteraction())
            EndInteraction();
        if (_interacting || _pressed || _thumbnail is null)
            return;

        try
        {
            if (ShownPart(_thumbnail.SourceSize) != _lastShown)
                FitToSource();
        }
        catch (WindowOperationException)
        {
            // Apps can be briefly unmeasurable while they redraw; the next tick tries again.
        }
    }

    /// <summary>Shows the close button while the pointer is on the view, and keeps it in the corner.</summary>
    private void FollowPointer()
        => UpdateHover(Cursor.Position, Environment.TickCount64);

    internal void UpdateHover(Point pointer, long now)
    {
        bool over = Bounds.Contains(pointer) || _titleBar?.Dragging == true;
        ClaimEscape(_selecting);
        if (!over || _suspended || _interacting || _selecting || _pressed || _overrides.ClickThrough)
        {
            _closeButton?.Hide();
            _titleBar?.Hide();
            DropHoverPreview();
            return;
        }

        if (_viewPreferences.CompactTitleBar)
        {
            if (_titleBar is null)
            {
                _titleBar = new PipTitleBar(this, () => _overrides.PositionLocked, SuspendView);
                KeepPartOutOfScreenCapture(_titleBar);
            }
            _titleBar.UpdateTitle(_origin.Title + " - " + InputDescription, _sourceIcon);
            _titleBar.Bounds = new Rectangle(Left + PipGeometry.Border, Top + PipGeometry.Border,
                Width - 2 * PipGeometry.Border, Dpi.Scale(28, DeviceDpi));
            if (!_titleBar.Visible) _titleBar.Show();
        }
        else ShowCloseButton();

        FollowHoverPreview(pointer, now);
    }

    private void ShowCloseButton()
    {

        int size = Dpi.Scale(CloseButtonSize, DeviceDpi);
        if (_closeButton is null)
        {
            _closeButton = new PipCloseButton(this, T("pip.close").Replace("&", ""), size);
            KeepPartOutOfScreenCapture(_closeButton);
        }

        _closeButton.Location = new Point(Right - size - PipGeometry.Border, Top + PipGeometry.Border);
        if (_closeButton.Visible)
            return;
        _closeButton.Show();
        User32.SetWindowPos(_closeButton.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    private void DropHoverPreview()
    {
        _hoverPreview?.Dispose();
        _hoverPreview = null;
        _hoverSince = 0;
    }

    private void FollowHoverPreview(Point pointer, long now)
    {
        if (_viewPreferences.HoverPreview == Settings.PipHoverPreview.Off || !_origin.IsWindow || _lastShown.IsEmpty ||
            _titleBar?.Dragging == true || _titleBar?.Bounds.Contains(pointer) == true)
        {
            DropHoverPreview();
            return;
        }
        if (_hoverPreview is null && (_hoverSince == 0 || pointer != _hoverPoint))
        {
            _hoverPoint = pointer;
            _hoverSince = now;
            return;
        }
        if (_hoverPreview is null && now - _hoverSince < _viewPreferences.HoverDelayMilliseconds) return;
        Rectangle shown = _lastShown;
        Rectangle area = Screen.FromControl(this).WorkingArea;
        bool lens = _viewPreferences.HoverPreview == Settings.PipHoverPreview.Lens;
        if (lens)
        {
            Point mapped = PipGeometry.PointToSource(PointToClient(pointer), PipGeometry.ViewArea(ClientSize), shown);
            shown = PipPreviewGeometry.Lens(shown, mapped, new Size(240, 160));
        }
        Size size = PipGeometry.FitLongSide(lens ? Dpi.Scale(400, DeviceDpi) : Math.Min(960, Math.Max(Width, Height) * 2), shown, area.Size);
        Rectangle avoid = lens ? new Rectangle(pointer, new Size(16, 16)) : Bounds;
        try
        {
            if (_hoverPreview is null)
            {
                _hoverPreview = new PipHoverPreview(this, _source);
                KeepPartOutOfScreenCapture(_hoverPreview);
            }
            _hoverPreview.UpdatePreview(shown, PipPreviewGeometry.Place(avoid, size, area));
        }
        catch (WindowOperationException)
        {
            DropHoverPreview(); // A source that refuses a thumbnail still has its ordinary view.
        }
    }

    /// <summary>Says when the picture may be out of date, without claiming more than WinScreen can tell.</summary>
    private void ShowState()
    {
        PipChipState state = PipStateChip.Decide(_origin.IsMinimized, BackendState,
            Bounds.Contains(Cursor.Position), _interacting || _selecting || _pressed);
        if (state == PipChipState.None)
        {
            _chip?.Hide();
            return;
        }

        if (_chip is null)
        {
            _chip = new PipStateChip(this);
            _chip.Chosen += KeepUpdating;
            KeepPartOutOfScreenCapture(_chip);
        }

        _chip.Show(state, new Point(Left + PipGeometry.Border, Bottom - PipGeometry.Border));
    }

    /// <summary>
    /// The person's way out of a minimized source: conceal it first so it never flashes up, then bring it
    /// back without taking the focus. Only a click on the chip does this, never WinScreen by itself.
    /// </summary>
    internal void KeepUpdating()
    {
        SetOriginalHidden(true);
        if (_hideItem.Checked && User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_SHOWNOACTIVATE);
        StayOnTop();
    }

    /// <summary>Leaves the view alone while it's being used; the mouse over it means it's wanted there.</summary>
    private void MoveAwayFromContent()
    {
        if (_pressed || _interacting || _selecting || _overrides.PositionLocked || MouseButtons != MouseButtons.None ||
            Bounds.Contains(Cursor.Position))
            return;

        Rectangle area = Screen.FromControl(this).WorkingArea;
        bool[,] busy;
        try
        {
            busy = ScreenContent.BusyCells(area);
        }
        catch (ExternalException)
        {
            // Windows refuses screen captures while the PC is locked or asking for permission.
            return;
        }

        if (PipGeometry.CalmerSpot(busy, area, Bounds) is { } spot)
            Location = spot;
    }

    private bool ShouldEndInteraction()
    {
        if (!_windows.IsPeeking(_source))
            return true;
        if (MouseButtons != MouseButtons.None)
            return false;
        if (User32.GetForegroundWindow() != _source)
            return true;
        if (User32.IsZoomed(_source))
            return Bounds.Contains(Cursor.Position);

        return !User32.GetWindowRect(_source, out Rect r)
               || !Rectangle.Inflate(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), LeaveMargin, LeaveMargin)
                   .Contains(Cursor.Position);
    }

    internal void GoToSource()
    {
        if (!_origin.IsWindow)
            return;
        if (!_origin.IsAvailable)
        {
            Close();
            return;
        }

        EndLive();
        EndInteraction();
        if (_windows.IsHidden(_source) || _windows.IsOutOfSwitcher(_source))
            SetOriginalHidden(false);
        if (_windows.IsHidden(_source) || _windows.IsOutOfSwitcher(_source))
            return;
        if (User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_RESTORE);
        User32.SetForegroundWindow(_source);
    }
}
