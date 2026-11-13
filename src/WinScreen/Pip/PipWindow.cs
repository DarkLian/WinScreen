using static WinScreen.Localization.UiText;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tray;
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
    private const int WheelNotch = 120;
    private const int StartLongSide = 480;
    private const int ScreenMargin = 20;
    private const int EscapeHotkey = 0x5750;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int WM_SETCURSOR = 0x0020, HTCLIENT = 1;

    private bool _cursorFromFrame;

    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int MinimumExclusionBuild = 19041;
    private const double ZoomStep = 1.25;
    private const int SmallLongSide = 320;
    private const int MediumLongSide = StartLongSide;
    private const int LargeLongSide = 720;

    private readonly PipSource _origin;
    private readonly nint _source;
    private readonly WindowController _windows;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _autoMove = new() { Interval = 1000 };
    private readonly ToolStripMenuItem _hideItem;
    private readonly ToolStripItem _goToItem;
    private readonly ToolStripItem _useOriginalItem;
    private readonly string _program;
    private bool _usingOriginal, _usingFromInteractive, _originalTookFocus;
    private long _originalLeftAt;
    private const long UseOriginalGrace = 400;
    private readonly ToolStripMenuItem _autoMoveItem;
    private readonly ToolStripMenuItem _removeCropItem;
    private readonly ToolStripMenuItem _viewOnlyItem;
    private readonly ToolStripMenuItem _helpItem;
    private readonly ToolStripItem _useHeader, _originalHeader;
    private readonly ContextMenuStrip _menu = new() { ShowItemToolTips = true };
    private PipTitleBar? _titleBar;
    private PipHoverPreview? _hoverPreview;
    private PipViewPreferences _viewPreferences = new();
    private Point _hoverPoint;
    private long _hoverSince;
    private Icon? _sourceIcon;
    private bool _suspended;
    private bool _inputPaused;
    private bool _passToShell;
    private bool _parkedCaptureFallback;
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
    private Rectangle _selection;
    private bool _cleaned;
    private PipInteraction? _live;
    private readonly System.Collections.Concurrent.ConcurrentQueue<PressOutcome> _pressOutcomes = new();
    private nint _foregroundBeforeLive;
    private bool _liveSawSourceFocus;
    private long _unobservedSince;
    private readonly ToolStripMenuItem _interactiveItem;
    private readonly ToolStripMenuItem _middleScrollItem;
    private readonly ToolStripMenuItem _enhancedHoverItem;
    private bool _hoverChosen;
    private bool _hiddenBeforeInteractive;
    private bool _wasScrolling;
    private SourcePopups? _popups;
    private int _popupTick;
    private bool _liveStartedZoomed;
    private System.Windows.Forms.Timer? _closeCheck;

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
        _program = source.IsWindow ? WindowInfo.ProgramOf(_source) : "";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black; // Whatever briefly shows while resizing shouldn't flash.
        SetStyle(ControlStyles.ResizeRedraw, true);
        StartPosition = FormStartPosition.Manual;
        Text = T("pip.titlePrefix") + source.Title;
        _longSide = Dpi.Scale(StartLongSide, source.Dpi);

        _hideItem = new ToolStripMenuItem(T("menu.original.keepHidden"), null,
            (_, _) => SetOriginalHidden(!_hideItem!.Checked));
        _autoMoveItem = new ToolStripMenuItem(T("pip.autoMove"), null,
            (_, _) => SetAutoMove(!_autoMoveItem!.Checked)).Experimental();
        _interactiveItem = new ToolStripMenuItem(T("menu.view.interactive"), null,
            (_, _) => SetInteractive(!_interactiveItem!.Checked)).Experimental().OneOf();
        _middleScrollItem = new ToolStripMenuItem(T("menu.view.middleScroll"), null,
            (_, _) => SetMiddleClickScroll(!_middleScrollItem!.Checked)) { ToolTipText = T("menu.view.middleScroll.tip") };
        _enhancedHoverItem = new ToolStripMenuItem(T("menu.view.hover"), null,
            (_, _) => { _hoverChosen = true; SetEnhancedHover(!_enhancedHoverItem!.Checked); })
            { ToolTipText = T("pip.enhancedHover.description"), Checked = PipEnhancedHover.StartsOn(_source) };
        _viewOnlyItem = new ToolStripMenuItem(T("menu.view.watch"), null, (_, _) => SetInteractive(false)) { Name = "viewOnly" }.OneOf();
        // The two options that only mean something while the picture is being used sit under Interactive PiP, set in.
        _middleScrollItem.Margin = _enhancedHoverItem.Margin = new Padding(MenuParts.Scale(16), 0, 0, 0);
        var options = new ToolStripMenuItem(T("menu.picture.options")).WithIcon(MenuParts.Sliders);
        options.DropDownItems.Add(new ToolStripSeparator()); // Windows only draws the arrow with something inside.
        options.DropDownOpening += (_, _) => FillOptions(options);
        var presets = new ToolStripMenuItem(T("pip.presets")).WithIcon(MenuParts.Star);
        presets.DropDownItems.Add(new ToolStripSeparator());
        presets.DropDownOpening += (_, _) => FillPresets(presets);
        _menu.Items.Add(new ToolStripMenuItem { Name = "inputStatus", Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(_useHeader = MenuParts.Header(T("menu.group.use")));
        _menu.Items.Add(_viewOnlyItem);
        _menu.Items.Add(_interactiveItem);
        _menu.Items.Add(_enhancedHoverItem);
        _menu.Items.Add(_middleScrollItem);
        _menu.Items.Add(_autoMoveItem);

        _menu.Items.Add(_originalHeader = MenuParts.Header(T("menu.group.original")));
        _hideItem.Image = MenuParts.Icon(MenuParts.View);
        _menu.Items.Add(_hideItem);
        _useOriginalItem = _menu.Items.Add(T("menu.original.use"), MenuParts.Icon(MenuParts.Undo), (_, _) => UseOriginal());
        _goToItem = _menu.Items.Add(T("menu.original.goTo"), MenuParts.Icon(MenuParts.NewWindow), (_, _) => GoToSource());

        _menu.Items.Add(MenuParts.Header(T("menu.group.picture")));
        _removeCropItem = new ToolStripMenuItem(T("pip.zoom.reset"), MenuParts.Icon(MenuParts.Crop),
            (_, _) => { ResetZoom(); SetCrop(Rectangle.Empty); });
        _menu.Items.Add(_removeCropItem);
        _menu.Items.Add(options);
        _menu.Items.Add(presets);

        _menu.Items.Add(new ToolStripSeparator());
        _helpItem = new ToolStripMenuItem(T("menu.help")).WithIcon(MenuParts.Help);
        _retryItem = new ToolStripMenuItem(T("pip.backend.retry"), null, (_, _) => RetryCapture());
        _helpItem.DropDownItems.Add(new ToolStripSeparator());
        _helpItem.DropDownItems.Add(T("pip.diagnostics.title"), null, (_, _) => ShowDiagnostics());
        _helpItem.DropDownItems.Add(_retryItem);
        _menu.Items.Add(_helpItem);
        _menu.Items.Add(T("pip.minimize"), MenuParts.Icon(MenuParts.Minimize), (_, _) => SuspendView());
        _menu.Items.Add(T("pip.close"), MenuParts.Icon(MenuParts.Close), (_, _) => Close());
        _menu.Opening += (_, _) =>
        {
            PauseInput(true, keepTitleBar: true);
            _titleBar?.HoldVisible();
            RefreshMenuItems();
            // A menu only closes on an outside click while its program has the focus.
            _foregroundBeforeMenu = User32.GetForegroundWindow();
            User32.SetForegroundWindow(Handle);
        };
        MenuRenderer.Apply(_menu);
        // Turning the view's options on and off leaves the menu open for the next one.
        foreach (ToolStripItem item in new ToolStripItem[] { _hideItem, _autoMoveItem, _interactiveItem, _viewOnlyItem, _middleScrollItem, _enhancedHoverItem })
            StayingOpen.Keep(item);
        StayingOpen.Follow(_menu, RefreshMenuItems);
        _menu.Closed += (_, _) => { ReturnFocusAfterMenu(); PauseInput(TopLayer.InputBlocked); };
        ContextMenuStrip = _menu;

        _watch.Tick += (_, _) => FollowSource();
        _autoMove.Tick += (_, _) => MoveAwayFromContent();
    }

    /// <summary>What the menu offers and ticks, for the view as it is now: when it opens, and after a choice that keeps it open.</summary>
    private void RefreshMenuItems()
    {
        _menu.Items["inputStatus"]!.Text = MenuTitle;
        FillHelp();
        _retryItem.Available = CanRetryCapture;
        _autoMoveItem.Available = AllowAutoMove;
        bool canInteract = AllowInteractive && _origin.IsWindow;
        _viewOnlyItem.Available = _interactiveItem.Available = canInteract;
        _viewOnlyItem.Checked = !_interactiveItem.Checked;
        // Available is what is asked for; Visible would also say whether the menu is open, and it isn't yet.
        bool showInteractionOptions = canInteract && _interactiveItem.Checked;
        _middleScrollItem.Available = showInteractionOptions;
        _enhancedHoverItem.Available = showInteractionOptions;
        _useHeader.Available = canInteract || AllowAutoMove;
        _enhancedHoverItem.Enabled = _enhancedHoverItem.Checked || PipEnhancedHover.CanAttempt(_source);
        // With Enhanced hover on, a browser's own middle-click scrolling works and is the better one.
        _middleScrollItem.Enabled = !(showInteractionOptions && _enhancedHoverItem.Checked && AppScrollsWithHover);
        _originalHeader.Available = _hideItem.Available = _goToItem.Available = _origin.IsWindow;
        _useOriginalItem.Available = _origin.IsWindow && _hideItem.Checked;
        // The view closes soon after its window does; until then nothing offers to act on a window that is gone.
        bool there = _origin.IsAvailable;
        _goToItem.Enabled = _useOriginalItem.Enabled = _interactiveItem.Enabled = there;
        if (!there) _hideItem.Enabled = false;
        _removeCropItem.Enabled = !_crop.IsEmpty || _zoom != PipGeometry.WholeView;
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
            PauseInput(TopLayer.InputBlocked);
            if (_inputPaused) TopLayer.KeepAbove();
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
        DropHoverPreview();
        // A view follows Settings for Enhanced hover until it is changed from its own menu.
        if (!_hoverChosen && PipEnhancedHover.StartsOn(_source) is var hover && hover != _enhancedHoverItem.Checked)
            SetEnhancedHover(hover);
    }

    internal bool IsSuspended => _suspended;
    internal nint SourceWindow => _source;
    internal bool SourceIsWindow => _origin.IsWindow;
    internal bool CanRetryCapture => _suspended || _thumbnail?.CanRetry == true;
    internal string InputDescription => _overrides.ClickThrough ? T("pip.state.clickThrough") :
        !_origin.IsWindow ? T("pip.input.viewOnly") :
        _interactiveItem.Checked ? T("pip.interactive") : T("pip.input.viewOnly");
    /// <summary>The first line of the view's menu: what it shows, and how it stands.</summary>
    internal string MenuTitle
    {
        get
        {
            string title = _origin.Title.ReplaceLineEndings(" ");
            if (title.Length > 40) title = title[..(char.IsHighSurrogate(title[39]) ? 39 : 40)] + "…";
            return title.Replace("&", "&&") + " - " + (_suspended ? T("pip.state.suspended") : _thumbnail?.StatusText ?? T("common.unavailable"));
        }
    }

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
        _usingOriginal = false; // A minimized view has no picture to hide the original behind; it stays shown.
        CancelPress();
        EndLive();
        CacheTaskbarPicture();
        _suspended = true;
        _resumeRequested = false;
        DropHoverPreview();
        _titleBar?.Dismiss();
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
            _thumbnail.SetViewport(PictureArea, _lastShown);
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
    public event Action<PipSourceBookmark>? SourceBookmarkSaved;

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

    private PipPreferences BackendPreferences()
    {
        PipPreferences result = _preferences.With(_overrides.ClickThrough ? new PipOverrides(OpacityPercent: 100) : new PipOverrides());
        result.RequireHiddenBorder = _origin.IsWindow && _windows.IsParked(_source);
        if (_parkedCaptureFallback) result.Backend = PipBackendPreference.Dwm;
        return result;
    }

    private void ConfigureParkedCapture()
    {
        bool fallback = _windows.IsParked(_source) &&
            (!CaptureBorder.TryHide(_ => { }, wait: 0) || _thumbnail?.CaptureBorderVisible == true);
        _parkedCaptureFallback = fallback;
        _thumbnail?.Configure(BackendPreferences());
        if (fallback && _preferences.Backend != PipBackendPreference.Dwm)
            Notice?.Invoke(T("pip.capture.hiddenStandard"));
    }

    /// <summary>Click-through needs the layered style too, or Windows still gives this window the clicks.</summary>
    private void ApplyClickThrough()
    {
        if (!IsHandleCreated)
            return;
        const long both = User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT;
        long style = User32.GetWindowLongPtr(Handle, User32.GWL_EXSTYLE);
        bool passes = _overrides.ClickThrough || _passToShell;
        long wanted = passes ? style | both : style & ~both;
        if (wanted != style)
            User32.SetWindowLongPtr(Handle, User32.GWL_EXSTYLE, (nint)wanted);
        if (passes)
            User32.SetLayeredWindowAttributes(Handle, 0, _overrides.ClickThrough ? _preferences.Alpha : (byte)255, User32.LWA_ALPHA);
    }

    /// <summary>One quiet line for each way to handle the picture, for how the view is being used now.</summary>
    private void FillHelp()
    {
        foreach (NoteRow old in _helpItem.DropDownItems.OfType<NoteRow>().ToArray())
        {
            _helpItem.DropDownItems.Remove(old);
            old.Dispose();
        }
        int at = 0;
        foreach (NoteRow line in MenuParts.Notes(_interactiveItem.Checked ? T("pip.controls.directHelp") : T("pip.controls.help")))
            _helpItem.DropDownItems.Insert(at++, line);
    }

    private void FillOptions(ToolStripMenuItem options)
    {
        options.DropDownItems.Clear();
        options.DropDownItems.Add(new NoteRow(
            _overrides.FrameRate is null && _overrides.Scaling is null && _overrides.OpacityPercent is null
                ? T("pip.defaults.inherited") : T("pip.defaults.overridden")));
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
        options.DropDownItems.Add(Toggle(T("pip.keepAspectRatio"), _overrides.KeepAspectRatio,
            () => Overrides = _overrides with { KeepAspectRatio = !_overrides.KeepAspectRatio }));
        options.DropDownItems.Add(Toggle(T("pip.lockPosition"), _overrides.PositionLocked,
            () => Overrides = _overrides with { PositionLocked = !_overrides.PositionLocked }));
        options.DropDownItems.Add(Toggle(T("pip.titleBarAlways"), TitleBarAlways,
            () => Overrides = _overrides with { TitleBarAlways = !TitleBarAlways }));
        options.DropDownItems.Add(new ToolStripMenuItem(T("pip.clickThrough"), null,
            (_, _) => Overrides = _overrides with { ClickThrough = true }));
        options.DropDownItems.Add(new ToolStripSeparator());
        var size = new ToolStripMenuItem(T("pip.size"));
        size.DropDownItems.Add(T("pip.size.small"), null, (_, _) => ResizeTo(Dpi.Scale(SmallLongSide, DeviceDpi)));
        size.DropDownItems.Add(T("pip.size.medium"), null, (_, _) => ResizeTo(Dpi.Scale(MediumLongSide, DeviceDpi)));
        size.DropDownItems.Add(T("pip.size.large"), null, (_, _) => ResizeTo(Dpi.Scale(LargeLongSide, DeviceDpi)));
        size.DropDownItems.Add(T("pip.size.fitSource"), null, (_, _) => FitToSourceSize());
        options.DropDownItems.Add(size);
        if (_origin is ScreenPipSource)
            options.DropDownItems.Add(T("pip.bookmarks.save"), null, (_, _) => SaveSourceBookmark());
    }

    private void FillPresets(ToolStripMenuItem presets)
    {
        presets.DropDownItems.Clear();
        IReadOnlyList<PipPreset> saved = Presets?.Invoke() ?? [];
        foreach (PipPreset preset in saved)
        {
            var entry = new ToolStripMenuItem(preset.Name.Replace("&", "&&"));
            entry.DropDownItems.Add(new NoteRow(PresetChanges(preset)));
            entry.DropDownItems.Add(new ToolStripSeparator());
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
    }

    private void SaveSourceBookmark()
    {
        if (_origin is not ScreenPipSource source) return;
        using var dialog = new Form { Text = T("pip.bookmarks.save"), AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, TopMost = true };
        var name = new TextBox { Width = 320, MaxLength = PipPreset.NameLimit, AccessibleName = T("pip.bookmarks.name") };
        var layout = new CheckBox { Text = T("pip.bookmarks.includeLayout"), AutoSize = true };
        var save = new Button { Text = T("common.save"), DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = T("common.cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(12) };
        page.Controls.AddRange([new Label { Text = T("pip.bookmarks.name"), AutoSize = true }, name, layout, save, cancel]);
        dialog.Controls.Add(page);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(name.Text)) return;
        Attempt(() =>
        {
            source.Require();
            Rectangle display = source.Screen.Bounds;
            PipPreset current = CurrentPreset(name.Text.Trim());
            var bookmark = new PipSourceBookmark
            {
                Name = name.Text.Trim(), Device = source.Device,
                DisplayX = display.X, DisplayY = display.Y, DisplayWidth = display.Width, DisplayHeight = display.Height,
                Region = source.Region is { } region ? PipCropShares.Of(region, display.Size) : null,
                View = layout.Checked ? new PipPreset { Name = current.Name, LongSide = current.LongSide,
                    Device = current.Device, Left = current.Left, Top = current.Top, PositionLocked = current.PositionLocked } : null,
            };
            SourceBookmarkSaved?.Invoke(bookmark);
        });
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
        if (preset.TitleBarAlways is { } always && always != TitleBarAlways) changes.Add(T("pip.titleBarAlways") + ": " + (always ? T("common.yes") : T("common.no")));
        return changes.Count == 0 ? T("pip.presets.noChanges") : string.Join("; ", changes);
    }

    /// <summary>A choice of several that leaves the menu open, ticked where it is now.</summary>
    private static ToolStripMenuItem Choices<T1>(string title, T1 current, (string Label, T1 Value)[] choices, Action<T1> choose)
    {
        var menu = new ToolStripMenuItem(title);
        foreach ((string label, T1 value) in choices)
            menu.DropDownItems.Add(StayingOpen.Keep(new ToolStripMenuItem(label, null, (sender, _) =>
            {
                choose(value);
                foreach (ToolStripMenuItem other in menu.DropDownItems.OfType<ToolStripMenuItem>()) other.Checked = other == sender;
            }) { Checked = EqualityComparer<T1>.Default.Equals(value, current) }.OneOf()));
        return menu;
    }

    /// <summary>An option turned on and off from the menu, which stays open for the next one.</summary>
    private static ToolStripMenuItem Toggle(string text, bool on, Action flip) =>
        StayingOpen.Keep(new ToolStripMenuItem(text, null, (sender, _) =>
        {
            flip();
            ((ToolStripMenuItem)sender!).Checked ^= true;
        }) { Checked = on });

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
            TitleBarAlways = _overrides.TitleBarAlways,
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
            TitleBarAlways = preset.TitleBarAlways ?? _overrides.TitleBarAlways,
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
    internal string DiagnosticSnapshot => (_thumbnail?.Snapshot() ?? "Stopped") + $"\nEnhanced hover: {_live?.HoverStatus ?? (_enhancedHoverItem.Checked ? "inactive" : "off")}";

    /// <summary>Opens the view's diagnostics in front, leaving the view usable while they are open.</summary>
    private void ShowDiagnostics()
    {
        string text = DiagnosticSnapshot;
        new Settings.InfoWindow(T("pip.diagnostics.title"), text, (T("pip.diagnostics.copy"), () =>
        {
            try { Clipboard.SetText(text.Replace("\n", Environment.NewLine)); }
            catch (ExternalException) { Problem?.Invoke(T("errors.clipboard.busy")); }
        }, true)).ShowInFront();
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
        if (_interactiveItem.Checked && !hidden) return;
        ChangeInteractionMode(_interactiveItem.Checked, hidden);
    }

    // All mode changes converge here. Menu state is published from actual concealment, including failure and
    // recovery; an interactive view never advertises a visible original as interactive.
    private void ChangeInteractionMode(bool interactive, bool hidden)
    {
        _usingOriginal = false; // Any other choice about the original replaces "use it, then hide it again".
        if (!interactive || !hidden) EndLive();
        bool changed = SetConcealment(hidden);
        PublishInteractionMode(interactive && changed);
    }

    private void PublishInteractionMode(bool interactive)
    {
        bool hidden = _origin.IsWindow && _origin.IsAvailable && _windows.IsConcealedBy(_source, this);
        if (!hidden) EndLive();
        _hideItem.Checked = hidden;
        _interactiveItem.Checked = interactive && hidden;
        _hideItem.Enabled = !_interactiveItem.Checked;
    }

    private bool SetConcealment(bool hidden)
    {
        if (!_origin.IsWindow)
            return false;
        if (!hidden) EndLive();
        try
        {
            _origin.Require();
            if (hidden && _thumbnail is null)
                throw new WindowOperationException(
                    T("errors.pip.previewNotReady"));
            if (hidden)
            {
                _windows.HideOwned(_source, this);
                if (SwitcherHidingStopsView) Problem?.Invoke(T("errors.switcher.windows10"));
                // A Store app keeps the taskbar button Windows owns; activating it shows the app (FollowSource).
                // Only its taskbar entry goes, which Windows 11 also leaves out of Alt+Tab; a tool window's frame
                // would replace the original's own caption buttons in the view.
                else if (!WindowController.TaskbarButtonOwnedByWindows(_source)) _windows.RemoveTaskbarButton(_source);
                _windows.Park(_source);
                PlaceForHover();
                ConfigureParkedCapture();
                if (User32.GetForegroundWindow() == _source)
                    User32.SetForegroundWindow(Handle); // Keys mustn't go to a window that can't be seen.
                StayOnTop();
            }
            else
            {
                _windows.ShowOwned(_source, this);
                _parkedCaptureFallback = false;
                _thumbnail?.Configure(BackendPreferences());
                _popups?.ShowAll();
            }
            return true;
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

            Problem?.Invoke(failure.Message);
            return false;
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
        }
        catch (WindowOperationException failure) { Problem?.Invoke(failure.Message); }
    }

    [DllImport("user32.dll")] private static extern int GetMessageTime();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == User32.WM_NCHITTEST && !_overrides.ClickThrough)
        {
            var point = new Point((short)m.LParam.ToInt64(), (short)(m.LParam.ToInt64() >> 16));
            int hit = _interactiveItem.Checked
                ? PipGeometry.SizingHit(point, Bounds, Dpi.Scale(PipGeometry.DirectEdgeBand, DeviceDpi), Dpi.Scale(PipGeometry.DirectCornerBand, DeviceDpi))
                : PipGeometry.SizingHit(point, Bounds, Dpi.Scale(PipGeometry.EdgeBand, DeviceDpi));
            if (hit != 0) { m.Result = hit; return; }
        }
        if (m.Msg == 0x0214 && _overrides.KeepAspectRatio && !_lastShown.IsEmpty)
        {
            Rect native = Marshal.PtrToStructure<Rect>(m.LParam);
            Rectangle bounds = PipGeometry.SizeWithAspect(Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom),
                (int)m.WParam, _lastShown);
            Marshal.StructureToPtr(new Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), m.LParam, false);
            m.Result = 1;
            return;
        }
        // A pointer move to the view itself: Windows has checked where the real pointer is (PipInteraction.MaxRecheckWait).
        if (m.Msg == 0x0200) _live?.ViewPointerMoved(GetMessageTime());
        // Keep the modifiers carried by the press. This inactive view may process it after Ctrl is released.
        if (m.Msg is 0x0201 or 0x0203)
            _controlAtPress = (m.WParam.ToInt64() & 0x0008) != 0; // MK_CONTROL
        if (m.Msg == WM_DISPLAYCHANGE || (m.Msg == 0x0218 && (m.WParam == 7 || m.WParam == 18)))
        {
            PipDisplay.InvalidateRefresh();
            _thumbnail?.DisplayChanged();
            if (m.Msg == WM_DISPLAYCHANGE) BeginInvokeIfAlive(FollowDisplays);
        }
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        if (m.Msg == WM_SETCURSOR && _live is not null)
        {
            // Over the picture the app being used decides the pointer's shape. Setting the arrow here on every
            // move would fight it, and the pointer would flicker between the two.
            if ((m.LParam.ToInt64() & 0xFFFF) == HTCLIENT)
            {
                if (_cursorFromFrame) Cursor.Current = Cursors.Default;
                _cursorFromFrame = false;
                // With input shared, the visit's input worker asks the app after each move it sends, and the
                // app's answer sets the pointer seen over the view. Asking here would make this thread wait on it.
                m.Result = 1;
                return;
            }
            _cursorFromFrame = true; // A sizing edge sets its own shape; coming back takes the arrow once.
        }

        if (m.Msg == User32.WM_HOTKEY && m.WParam == EscapeHotkey)
        {
            _live?.CancelScroll();
            CancelPress();
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// The displays changed (a monitor came or went, or a resolution changed): the hidden original goes past an outer
    /// edge of the new layout, and the view stays on a screen it can be reached on.
    /// </summary>
    internal void FollowDisplays()
    {
        if (_cleaned || IsDisposed) return;
        if (_origin.IsWindow && _origin.IsAvailable && _windows.IsParked(_source))
        {
            EndLive(); // Its mapping was for the old layout; the next move starts a new visit.
            Attempt(() => _windows.Repark(_source));
        }
        Rectangle area = Screen.FromControl(this).WorkingArea;
        if (!area.Contains(Bounds)) Location = PipGeometry.KeepInside(Bounds, area);
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
            Rectangle shown = PictureArea;
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
            Rectangle area = PictureArea;
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
                PictureArea);
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
        // A program listed in Settings as not working through a view: a plain click shows its original instead.
        if (!_selecting && !_dragged && !_controlAtPress && _live is null && OpensOriginalOnClick && PictureArea.Contains(e.Location))
        {
            UseOriginal();
            return;
        }
        if (_selecting)
        {
            EndSelection();
            if (_selection.Width < 10 || _selection.Height < 10 || _thumbnail is null)
            {
                SetCrop(_crop);
            }
            else
            {
                Rectangle part = PipGeometry.SelectionToSource(_selection, PictureArea,
                    ShownPart(_thumbnail.SourceSize));
                part.Offset(-_whole.X, -_whole.Y);
                SetCrop(part);
            }
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
        // Keep the recovery surface reachable if a concealed source is temporarily unresponsive.
        if (_origin.IsWindow && _origin.IsAvailable &&
            (_windows.IsHidden(_source) || _windows.IsOutOfSwitcher(_source)))
        {
            ChangeInteractionMode(false, false);
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
        if (_hideItem.Checked && _origin.IsAvailable)
            ChangeInteractionMode(false, false);
        _popups?.ShowAll();
        _closeCheck?.Dispose();
        DropHoverPreview();
        _titleBar?.Dispose();
        _sourceIcon?.Dispose();
        _taskbarPicture?.Dispose();
        _chip?.Dispose();
        _menu.Dispose();
    }

    /// <summary>Interactive use requires concealment; turning it off restores the prior hiding preference.</summary>
    internal void SetInteractive(bool on)
    {
        if (on == _interactiveItem.Checked) return;
        if (on && (!AllowInteractive || !_origin.IsWindow)) return;
        if (on)
        {
            _hiddenBeforeInteractive = _hideItem.Checked;
            ChangeInteractionMode(true, true);
            if (_interactiveItem.Checked && _overrides.ClickThrough)
                Overrides = _overrides with { ClickThrough = false };
        }
        else
        {
            ChangeInteractionMode(false, _hiddenBeforeInteractive);
        }
    }

    internal PipInteraction? LiveInteraction => _live;

    /// <summary>True for a hidden original whose program is listed in Settings as one to use directly.</summary>
    internal bool OpensOriginalOnClick => _origin.IsWindow && _hideItem.Checked && PipOriginalApps.Includes(_program);

    /// <summary>True while the original is on show to be used, and will be hidden again afterwards.</summary>
    internal bool UsingOriginal => _usingOriginal;

    /// <summary>
    /// The answer for an app that doesn't work through a view: its hidden original comes back where it was, to be
    /// used as it is, and goes away again once another window is in use. Nothing is sent to the app.
    /// </summary>
    internal void UseOriginal()
    {
        if (!_origin.IsWindow || !_hideItem.Checked || _usingOriginal) return;
        bool interactive = _interactiveItem.Checked;
        GoToSource();
        if (IsDisposed || !_origin.IsAvailable || _windows.IsHidden(_source)) return;
        _usingOriginal = true;
        _usingFromInteractive = interactive;
        _originalTookFocus = User32.GetForegroundWindow() == _source;
        _originalLeftAt = 0;
    }

    /// <summary>
    /// Hides the original again once the focus has been with it and has left for another window. Its own dialogs
    /// and menus count as still using it; a moment with no window in front is only passing.
    /// </summary>
    private void FollowOriginalInUse()
    {
        if (!_usingOriginal) return;
        if (_suspended || _thumbnail is null || _windows.IsHidden(_source)) { _usingOriginal = false; return; }
        nint foreground = User32.GetForegroundWindow();
        User32.GetWindowThreadProcessId(_source, out uint process);
        if (foreground == _source || (foreground != 0 && SourcePopups.IsPopupOf(foreground, _source, process)))
        {
            _originalTookFocus = true;
            _originalLeftAt = 0;
            return;
        }
        if (!_originalTookFocus || foreground == 0 || _menu.Visible) return;
        long now = Environment.TickCount64;
        if (_originalLeftAt == 0) _originalLeftAt = now;
        if (now - _originalLeftAt < UseOriginalGrace) return;
        bool interactive = _usingFromInteractive && AllowInteractive;
        ChangeInteractionMode(interactive, true);
        if (_hideItem.Checked && User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_SHOWNOACTIVATE); // Minimized while in use: it has no picture until restored.
        StayOnTop();
    }

    internal void SetEnhancedHover(bool enabled)
    {
        bool restart = _live is not null;
        if (restart) EndLive();
        _enhancedHoverItem.Checked = enabled;
        if (restart) BeginLive(PointToClient(Cursor.Position));
        else PlaceForHover();
    }

    /// <summary>
    /// A hidden browser or Electron app keeps its strip at a side edge the whole time, whether or not Enhanced hover
    /// is on yet. Under the taskbar it finds itself covered, stops drawing and taking pointer moves, and doesn't look
    /// again until it is clicked; moving it once the option is turned on, or at the start of a visit, is too late.
    /// Other programs the helper can be used with go there only while the option is on.
    /// </summary>
    private bool HoverPlacementWanted => PipEnhancedHover.Supported && PipEnhancedHover.Describe(_source) is var (app, native) && native &&
        (app is PipHoverApp.Browser or PipHoverApp.Firefox or PipHoverApp.Electron || (app != PipHoverApp.None && _enhancedHoverItem.Checked));

    private bool AppScrollsWithHover => PipEnhancedHover.Supported && PipEnhancedHover.Describe(_source) is var (app, native) && native &&
        PipHoverApps.Recommended(app);

    private void PlaceForHover()
    {
        if (!_origin.IsWindow || !_windows.IsParked(_source)) return;
        try { _windows.SetParkedHoverSurface(_source, HoverPlacementWanted); }
        catch (WindowOperationException failure) { Problem?.Invoke(failure.Message); }
    }

    internal void SetMiddleClickScroll(bool enabled)
    {
        _middleScrollItem.Checked = enabled;
        if (_live is { } live) { live.MiddleClickScroll = enabled; if (!enabled) live.CancelScroll(); }
    }

    /// <summary>Why the last visit ended, for tests and diagnostics.</summary>
    internal string LastVisitEnd { get; private set; } = "";

    /// <summary>Where the title bar is while it shows, for tests.</summary>
    internal Rectangle? TitleBarBounds => _titleBar is { Visible: true } bar ? bar.Bounds : null;

    /// <summary>Every press's outcome over this view's life, so none can pass unnoticed in tests.</summary>
    internal IReadOnlyCollection<PressOutcome> PressOutcomes => _pressOutcomes;

    /// <summary>The pointer came onto the view. Everything that lets real input through exists from here until it leaves.</summary>
    private SourcePopups CreateSourcePopups() => new(_source)
    {
        RaisePopups = popups => BeginInvokeIfAlive(() =>
        {
            if (_cleaned || _live is null || !_origin.IsAvailable) return;
            User32.GetWindowThreadProcessId(_source, out uint sourceProcess);
            nint[] own = popups.Where(popup => User32.IsWindow(popup) && SourcePopups.IsPopupOf(popup, _source, sourceProcess)).ToArray();
            if (own.Length != 0) TopLayer.RaiseGroup(own);
        }),
    };

    private void BeginLive(Point pointer)
    {
        if (_live is not null || _thumbnail is null || _suspended || !_origin.IsAvailable || !_windows.IsHidden(_source) ||
            _inputPaused || _menu.Visible || TopLayer.InputBlocked || PipOriginalApps.Includes(_program))
            return;
        Rectangle area = PictureArea;
        if (!area.Contains(pointer))
            return;
        _foregroundBeforeLive = User32.GetForegroundWindow();
        _liveSawSourceFocus = false;
        _unobservedSince = 0;
        _popups ??= CreateSourcePopups();
        var live = new PipInteraction(_source, RectangleToScreen(area), ShownPart(_thumbnail.SourceSize), Handle, _popups);
        live.MiddleClickScroll = _middleScrollItem.Checked;
        live.AppScrollsWithHover = AppScrollsWithHover;
        live.EnhancedHover = _enhancedHoverItem.Checked;
        live.Left += () => BeginInvokeIfAlive(() => { LastVisitEnd = "the pointer left"; EndLive(); });
        live.Pressed += _pressOutcomes.Enqueue;
        live.ViewDragged += moved => BeginInvokeIfAlive(() => DragLiveView(moved));
        live.CaptionPressed += action => BeginInvokeIfAlive(() => OnCaption(action));
        live.QuestionShown += () => BeginInvokeIfAlive(OnQuestion);
        _liveStartedZoomed = User32.IsZoomed(_source) || CoversAMonitor(_source);
        try
        {
            _windows.Park(_source);
            _windows.SetParkedHoverSurface(_source, HoverPlacementWanted);
            live.Start(PointToScreen(pointer));
            _live = live;
            _windows.SetParkedReachable(_source, true);
            _cursorFromFrame = true; // Whatever shape the pointer arrived with goes back to the arrow once.
        }
        catch (WindowOperationException failure)
        {
            _live = null;
            live.Dispose();
            try { _windows.SetParkedReachable(_source, false); _windows.SetParkedHoverSurface(_source, false); } catch (WindowOperationException) { }
            SetInputPassesThrough(false);
            _interactiveItem.Checked = false;
            Problem?.Invoke(failure.Message);
        }
    }

    private void BeginInvokeIfAlive(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
    }

    private void DragLiveView(Size moved)
    {
        if (_live is null || _overrides.PositionLocked)
            return;
        Location += moved;
        if (_thumbnail is not null)
            _live.Update(RectangleToScreen(PictureArea), ShownPart(_thumbnail.SourceSize));
    }

    /// <summary>In this order: the source stops taking clicks, goes home, the hook goes, the view takes input, keys come back.</summary>
    private void EndLive()
    {
        ClaimEscape(_selecting); // Scrolling must not keep a global Escape claim after a visit/suspension ends.
        if (_live is not { } live)
            return;
        _live = null;
        _liveEndedAt = Environment.TickCount64;
        try { if (_origin.IsAvailable) { _windows.SetParkedReachable(_source, false); _windows.SetParkedHoverSurface(_source, HoverPlacementWanted); } }
        catch (WindowOperationException failure) { Problem?.Invoke(failure.Message); }
        live.Dispose();
        Cursor.Current = Cursors.Default;
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
    /// crop drag or optional scrolling: a pointer merely resting here mustn't take Esc from another app.
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
        if (!_inputPaused && !_menu.Visible && !TopLayer.InputBlocked) TopLayer.Raise(Handle);
    }

    private void PauseInput(bool paused, bool keepTitleBar = false)
    {
        if (_inputPaused == paused) return;
        _inputPaused = paused;
        if (!paused) _passToShell = false;
        if (paused)
        {
            CancelPress();
            EndLive();
            DropHoverPreview();
            if (!keepTitleBar && !_menu.Visible) _titleBar?.Dismiss();
        }
        ApplyClickThrough();
    }

    /// <summary>Windows lifts an activated always-on-top window over the taskbar; this puts it back under.</summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (IsHandleCreated) StayOnTop();
    }

    /// <summary>Ends an Interactive PiP visit, making the parked original unreachable again.</summary>
    internal void EndVisit() => EndLive();

    /// <summary>The original's own close, minimize and maximize buttons, and its icon, clicked in the view.</summary>
    private void OnCaption(CaptionAction action)
    {
        switch (action)
        {
            case CaptionAction.Close:
                EndLive();
                User32.PostMessage(_source, 0x0112 /* WM_SYSCOMMAND */, 0xF060 /* SC_CLOSE */, 0);
                // The view closes with the original. If it's still there, it asked something: show it.
                _closeCheck?.Dispose();
                _closeCheck = new System.Windows.Forms.Timer { Interval = 1000 };
                _closeCheck.Tick += (_, _) =>
                {
                    _closeCheck?.Dispose();
                    _closeCheck = null;
                    if (!IsDisposed && _origin.IsAvailable) GoToSource();
                };
                _closeCheck.Start();
                break;
            case CaptionAction.Minimize:
                SuspendView();
                break;
            case CaptionAction.GoToOriginal:
                GoToSource();
                break;
            case CaptionAction.Menu:
                EndLive();
                _menu.Show(Cursor.Position);
                break;
        }
    }

    /// <summary>The original asks something: a question nobody can see mustn't wait, so show the original.</summary>
    private void OnQuestion()
    {
        LastVisitEnd = "the original asked something";
        EndLive();
        if (_origin.IsAvailable) GoToSource();
    }

    /// <summary>
    /// What the original does by itself while used directly, from a button it draws as content: minimizing
    /// minimizes the view instead (a minimized window has no picture), and maximizing goes to the original.
    /// </summary>
    private void FollowOriginalState()
    {
        if (!_interactiveItem.Checked || _suspended || !_origin.IsAvailable)
            return;
        if (User32.IsIconic(_source) && _windows.IsHidden(_source))
        {
            EndLive();
            User32.ShowWindow(_source, User32.SW_SHOWNOACTIVATE);
            SuspendView();
        }
        else if (_live is not null && !_liveStartedZoomed && CoversAMonitor(_source))
        {
            // A page went full screen: leaving it can put the window back maximized, which is no request to see it.
            _liveStartedZoomed = true;
        }
        else if (_live is not null && !_liveStartedZoomed && User32.IsZoomed(_source))
        {
            EndLive();
            GoToSource();
        }
    }

    private static bool CoversAMonitor(nint window) =>
        User32.GetWindowRect(window, out Rect r) &&
        WindowController.FillsAMonitor(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), [.. Screen.AllScreens.Select(s => s.Bounds)]);

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
        view.SetViewport(PictureArea, shown);
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
        _thumbnail.SetViewport(PictureArea, shown);
    }

    /// <summary>
    /// A hidden original that something else brings to the front (its taskbar button, Alt+Tab, a notification, the
    /// app itself) is shown, so nobody types into a window they can't see. A visit's own press doesn't count: it
    /// happens while the visit is on, and the moment after it ends is left alone while the focus goes back.
    /// </summary>
    private void RevealIfActivatedElsewhere()
    {
        bool focused = _origin.IsWindow && User32.GetForegroundWindow() == _source;
        bool arrived = focused && !_sourceWasFocused;
        _sourceWasFocused = focused;
        if (!arrived || _live is not null || !_hideItem.Checked || Environment.TickCount64 - _liveEndedAt < RevealGrace)
            return;
        LastReveal = Environment.TickCount64;
        if (_suspended) ResumeView();
        GoToSource();
    }

    private bool _sourceWasFocused;
    private long _liveEndedAt;
    private const long RevealGrace = 500;

    /// <summary>When a hidden original was last shown because something else activated it, for tests.</summary>
    internal long LastReveal { get; private set; }

    private void FollowSource()
    {
        if (!_origin.IsAvailable)
        {
            PublishInteractionMode(false);
            if (_origin.IsWindow) Close();
            else if (!_suspended) SuspendView();
            return;
        }
        if (_resumeRequested && _retiringCapture.IsCompleted) ResumeView();
        PublishInteractionMode(_interactiveItem.Checked);

        string title = T("pip.titlePrefix") + _origin.Title;
        if (Text != title) Text = title;
        RevealIfActivatedElsewhere();
        FollowOriginalInUse();
        if (_suspended) return;

        try
        {
            _thumbnail?.Poll(ClientSize);
            if (!_parkedCaptureFallback && _origin.IsWindow && _windows.IsParked(_source) && _thumbnail?.CaptureBorderVisible == true)
                ConfigureParkedCapture();
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
        try { _windows.FollowParkedPlace(_source); } catch (WindowOperationException) { }
        PauseInput(_menu.Visible || TopLayer.InputBlocked);
        // The taskbar's overflow panel can open over a view; the pointer goes through to it. WinScreen's own menus
        // need no such thing: a click on the view while its menu is open just closes the menu.
        bool passToShell = _inputPaused && TopLayer.OverflowOpen;
        if (passToShell != _passToShell) { _passToShell = passToShell; ApplyClickThrough(); }
        FollowOriginalState();
        if (_suspended || IsDisposed) return;
        // A hidden original's link text and tooltips would show where it really is; every fifth tick is enough.
        if (_live is null && _hideItem.Checked && ++_popupTick % 5 == 0)
            (_popups ??= CreateSourcePopups()).Sweep(null);
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
            _live.Update(RectangleToScreen(PictureArea), ShownPart(_thumbnail.SourceSize));
            nint foreground = User32.GetForegroundWindow();
            User32.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
            User32.GetWindowThreadProcessId(_source, out uint sourceProcess);
            if (foreground == _source) _liveSawSourceFocus = true;
            // No foreground window at all is a moment in passing, while a click hands the focus over.
            bool switchedAway = foreground != 0 && foregroundProcess != sourceProcess && foregroundProcess != Environment.ProcessId &&
                (_liveSawSourceFocus || foreground != _foregroundBeforeLive);
            if (_live.ObservedPointer(Cursor.Position)) _unobservedSince = 0;
            else if (_unobservedSince == 0) _unobservedSince = Environment.TickCount64;
            bool hookLost = _unobservedSince != 0 && Environment.TickCount64 - _unobservedSince > 150;
            // Windows drops a hook it thinks is slow. The source must not stay clickable if that happens.
            if (switchedAway || hookLost || !Bounds.Contains(Cursor.Position) && MouseButtons == MouseButtons.None && !_live.InPopup)
            {
                LastVisitEnd = switchedAway ? $"switched to 0x{foreground:X} ({WindowInfo.ClassName(foreground)})"
                    : hookLost ? "the hook stopped seeing the pointer" : "the pointer was outside";
                EndLive();
            }
        }

        if (_pressed || _thumbnail is null)
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

    private void FollowPointer() => UpdateHover(Cursor.Position, Environment.TickCount64);

    internal void UpdateHover(Point pointer, long now)
    {
        ClaimEscape(_selecting || _live?.Scrolling == true);
        if (_live?.Scrolling == true) Cursor.Current = _live.ScrollCursor;
        else if (_wasScrolling) Cursor.Current = Cursors.Default;
        _wasScrolling = _live?.Scrolling == true;
        bool unavailable = _suspended || _selecting || _overrides.ClickThrough || (_inputPaused && !_menu.Visible);
        if (unavailable)
        {
            _titleBar?.Dismiss();
            DropHoverPreview();
            return;
        }
        if (_titleBar is null)
        {
            _titleBar = new PipTitleBar(this, () => _overrides.PositionLocked, SuspendView);
            KeepPartOutOfScreenCapture(_titleBar);
            _titleBar.MenuRequested += point => _menu.Show(point);
            PositionTitleBar();
        }
        _titleBar.UpdateTitle(_origin.Title, _sourceIcon);
        if (_titleBar.Width != _titleBar.FittedWidth(Width)) PositionTitleBar(); // Titles change, as tabs do.
        bool over = Bounds.Contains(pointer) || _titleBar.Dragging ||
            (_titleBar.Visible && _titleBar.Bounds.Contains(pointer));
        if (_menu.Visible || _titleBar.Dragging || TitleBarAlways) _titleBar.HoldVisible();
        else _titleBar.FollowHover(over, now);
        if (over && !_pressed && !_menu.Visible) FollowHoverPreview(pointer, now);
        else DropHoverPreview();
    }

    /// <summary>The title bar stays up for this view: its own choice, or else the one in Settings.</summary>
    internal bool TitleBarAlways => _overrides.TitleBarAlways ?? _viewPreferences.AlwaysShowTitleBar;

    private Rectangle PictureArea => PipGeometry.FitPicture(PipGeometry.ViewArea(ClientSize), _lastShown);

    private void PositionTitleBar()
    {
        if (_titleBar is null || IsDisposed || _titleBar.Dragging) return;
        int height = Dpi.Scale(28, DeviceDpi);
        Rectangle area = Screen.FromControl(this).WorkingArea;
        User32.SetWindowPos(_titleBar.Handle, 0, Left, Top - height >= area.Top ? Top - height : Bottom,
            _titleBar.FittedWidth(Width), height, User32.SWP_NOACTIVATE | User32.SWP_NOZORDER);

    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        PositionTitleBar();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionTitleBar();
        if (_thumbnail is not null && !_lastShown.IsEmpty && ClientSize.Width > 4 && ClientSize.Height > 4)
        {
            _longSide = Math.Max(Width, Height);
            Attempt(() => _thumbnail.SetViewport(PictureArea, _lastShown));
        }
    }

    private void DropHoverPreview()
    {
        _hoverPreview?.Dispose();
        _hoverPreview = null;
        _hoverSince = 0;
    }

    private void FollowHoverPreview(Point pointer, long now)
    {
        if (_viewPreferences.HoverPreview == Settings.PipHoverPreview.Off ||
            (_viewPreferences.HoverPreview == Settings.PipHoverPreview.WholeView &&
             !PipGeometry.NeedsWholePreview(PictureArea.Width, _lastShown.Width)) || !_origin.IsWindow || _lastShown.IsEmpty ||
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
            Point mapped = PipGeometry.PointToSource(PointToClient(pointer), PictureArea, shown);
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
            bool appearing = !_hoverPreview.Visible;
            _hoverPreview.UpdatePreview(shown, PipPreviewGeometry.Place(avoid, size, area));
            if (appearing) WinScreen.Windows.TopLayer.Raise(_hoverPreview.Handle);
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
            Bounds.Contains(Cursor.Position), _selecting || _pressed);
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
        if (_pressed || _selecting || _overrides.PositionLocked || MouseButtons != MouseButtons.None ||
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

    internal void GoToSource()
    {
        if (!_origin.IsWindow)
            return;
        if (!_origin.IsAvailable)
        {
            Close();
            return;
        }

        ChangeInteractionMode(false, false);
        if (_windows.IsHidden(_source) || _windows.IsOutOfSwitcher(_source))
            return;
        if (User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_RESTORE);
        User32.SetForegroundWindow(_source);
    }
}
