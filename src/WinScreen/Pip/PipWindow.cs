using static WinScreen.Localization.UiText;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;

namespace WinScreen.Pip;

internal enum PipStateProblem
{
    None,
    InteractiveWithOriginalShown,
    VisitWithoutInteractive,
    VisitWhilePaused,
    UsingHiddenOriginal,
    UsingOriginalWhileMinimized,
    InteractiveNotAllowed,
    ExperimentalVisitWithoutSwitch,
    DropDuringVisit,
    DropWithoutHiding,
    AwayWhileHiddenOrInUse,
    MinimizedWhileHiddenOrInUse,
}

// Never takes the focus from the app in use.
public sealed partial class PipWindow : Form
{
    private const int WheelStep = 40;
    private const int SnapReach = 8;
    private const int DragThreshold = 4;
    private const int WheelNotch = 120;
    private const int StartLongSide = 480;
    private const int ScreenMargin = 20;
    private const int EscapeHotkey = 0x5750;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int WM_SETCURSOR = 0x0020, HTCLIENT = 1;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;

    private bool _cursorFromFrame;

    private const int WM_DISPLAYCHANGE = 0x007E;
    private const double ZoomStep = 1.25;
    private const int SmallLongSide = 320;
    private const int MediumLongSide = StartLongSide;
    private const int LargeLongSide = 720;

    private readonly PipSource _origin;
    private readonly nint _source;
    private readonly WindowController _windows;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _autoMove = new() { Interval = 1000 };
    private ToolStripMenuItem _hideItem;
    private ToolStripMenuItem _awayItem;
    private ToolStripItem _goToItem;
    private ToolStripMenuItem _muteItem;
    private ToolStripItem _useOriginalItem;
    private readonly string _program;
    private readonly bool _javaSource;

    private bool ShowsOriginalOnly => _javaSource || PipOriginalApps.Includes(_program);
    private bool _usingOriginal, _usingFromInteractive, _originalTookFocus;
    private long _originalLeftAt;
    private const long UseOriginalGrace = 400;
    private ToolStripMenuItem _autoMoveItem;
    private ToolStripMenuItem _removeCropItem;
    private ToolStripMenuItem _freezeItem;
    private bool _frozen;
    private ToolStripMenuItem _turnItem, _uprightItem;
    private ViewTurn _turn, _appliedTurn;
    private ToolStripMenuItem _viewOnlyItem;
    private ToolStripMenuItem _helpItem;
    private ToolStripItem _useHeader, _originalHeader;
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
    private bool _fitting;
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
    private readonly PipPressFeedback _pressFeedback;
    private PressOffer _offer;
    private long _offerUntil;
    private const long OfferShownFor = 6000;
    private nint _foregroundBeforeLive;
    private bool _liveSawSourceFocus;
    private long _unobservedSince;
    private ToolStripMenuItem _interactiveItem;
    private ToolStripMenuItem _middleScrollItem;
    private ToolStripMenuItem _enhancedHoverItem;
    private bool _hoverChosen;
    private bool _hiddenBeforeInteractive, _minimizedBeforeInteractive, _offTaskbarBeforeInteractive;
    private bool _wasScrolling;
    private SourcePopups? _popups;
    private int _popupTick;
    private bool _liveStartedZoomed;
    private System.Windows.Forms.Timer? _closeCheck;

    [System.ComponentModel.DefaultValue(false)]
    public bool AllowAutoMove { get; set; }

    [System.ComponentModel.DefaultValue(false)]
    public bool AllowInteractive
    {
        get => _allowInteractive;
        set
        {
            if (_allowInteractive == value) return;
            _allowInteractive = value;
            if (!CanInteract) { SetInteractive(false); return; }
            // Enhanced hover and middle-click scrolling come and go with the switch: a visit going on starts again.
            if (_live is not null) { EndLive(); BeginLive(PointToClient(Cursor.Position)); }
            PlaceForHover();
        }
    }
    private bool _allowInteractive;

    private readonly bool _interactiveWithoutSwitch;

    internal bool CanInteract => (_allowInteractive || _interactiveWithoutSwitch) && _origin.IsWindow;

    private bool HoverOn => _enhancedHoverItem.Checked && _allowInteractive;

    private bool MiddleScrollOn => _middleScrollItem.Checked && _allowInteractive;

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
        _javaSource = source.IsWindow && PipOriginalApps.IsJavaWindow(_source);
        _pressFeedback = new PipPressFeedback(_program);
        _interactiveWithoutSwitch = source.IsWindow && PipEnhancedHover.InteractiveWithoutSwitch(_source);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black; // Whatever briefly shows while resizing shouldn't flash.
        SetStyle(ControlStyles.ResizeRedraw, true);
        StartPosition = FormStartPosition.Manual;
        Text = T("pip.titlePrefix") + source.Title;
        _longSide = Dpi.Scale(StartLongSide, source.Dpi);

        BuildMenu();

        _watch.Tick += (_, _) => FollowSource();
        _autoMove.Tick += (_, _) => MoveAwayFromContent();
        // A drag over the view goes to its original: see OnDragEnter. Windows takes drops only on a thread set up for
        // them,
        // as WinScreen's own is; anywhere else the view would fail to open.
        AllowDrop = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
    }

    public event Action<string>? Problem;

    public event Action<string>? Notice;

    public event Action<string>? AlwaysShowOriginal;

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

    // Created on top rather than pinned afterwards: pinning needs the focus, which Windows can refuse and which belongs
    // to the app in use.
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
        if (!_hoverChosen && PipEnhancedHover.StartsOn(_source) is var hover && hover != _enhancedHoverItem.Checked)
            SetEnhancedHover(hover);
    }

    internal bool IsSuspended => _suspended;

    internal long SuspendedAt => _suspendedAt;
    internal nint SourceWindow => _source;
    internal bool SourceIsWindow => _origin.IsWindow;
    internal bool CanRetryCapture => _suspended || _thumbnail?.CanRetry == true;
    internal string InputDescription => _overrides.ClickThrough ? T("pip.state.clickThrough") :
        !_origin.IsWindow ? T("pip.input.viewOnly") :
        _interactiveItem.Checked ? T("pip.interactive") : T("pip.input.viewOnly");
    internal string MenuTitle
    {
        get
        {
            string title = _origin.Title.ReplaceLineEndings(" ");
            if (title.Length > 40) title = title[..(char.IsHighSurrogate(title[39]) ? 39 : 40)] + "…";
            return title.Replace("&", "&&") + " - " + (_suspended ? T("pip.state.suspended") : Frozen ? T("pip.state.frozen")
                : _thumbnail?.StatusText ?? T("common.unavailable"));
        }
    }

    internal string StateDescription => string.Join(", ", new[]
    {
        _suspended ? T("pip.state.suspended") : _thumbnail?.StatusText ?? T("common.unavailable"),
        Frozen ? T("pip.state.frozen") : "",
        InputDescription,
        _hideItem.Checked ? T("pip.state.originalHidden") : "",
        _awayFrom != Guid.Empty ? T("pip.state.awayHidden") : "",
        _minimizedOriginal ? T("pip.state.minimizedHidden") : "",
    }.Where(value => value.Length > 0));

    internal void RetryCapture()
    {
        if (_suspended) ResumeView();
        else _thumbnail?.Retry();
    }

    internal void SuspendView()
    {
        if (_cleaned) return;
        ViewState.Modes before = CurrentModes();
        if (ViewState.Apply(before, ViewState.Request.Minimize) == before) return;
        _usingOriginal = false;
        EndLift(CoverEnd.ViewMinimized);
        EndDrop();
        StepAside(false);
        CancelPress();
        EndLive();
        CacheTaskbarPicture();
        _suspended = true;
        _suspendedAt = Environment.TickCount64;
        _resumeRequested = false;
        DropHoverPreview();
        _titleBar?.Dismiss();
        _chip?.Hide();
        _autoMove.Stop();
        _thumbnail?.Dispose();
        _retiringCapture = _thumbnail?.Retirement ?? Task.CompletedTask;
        _thumbnail = null;
        // Its frame goes with the capture, so the view comes back live.
        _frozen = false;
        Hide();
        if (_viewPreferences.MinimizeToTaskbar && _sourceIcon is not null)
            TaskbarProxy = new PipTaskbarProxy(_origin.Title, _sourceIcon, Bounds, TaskbarPreview, ResumeView, Close);
        CheckState();
        CheckRequest(before, ViewState.Request.Minimize);
        Diagnostics.Log.Event("view.minimized", ("program", Diagnostics.LogValue.ProgramOf(_source)), ("hidden", _hideItem.Checked),
            ("foreground", Diagnostics.LogValue.ProgramOf(User32.GetForegroundWindow())));
    }

    internal PipTaskbarProxy? TaskbarProxy { get; private set; }

    private void DropTaskbarProxy()
    {
        TaskbarProxy?.Dismiss();
        TaskbarProxy = null;
    }

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
            Diagnostics.Log.Swallowed(failure);
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
        ViewState.Modes before = CurrentModes();
        try
        {
            _thumbnail = new PipBackendSelector(_origin, Handle, BackendPreferences());
            _thumbnail.Problem += text => Problem?.Invoke(text);
            // A new capture starts on Windows' thumbnail, which shows upright; the turn comes back once capture draws.
            if (!_appliedTurn.IsUpright)
            {
                _appliedTurn = ViewTurn.Upright;
                Attempt(() => FitToSource());
            }
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
        CheckState();
        CheckRequest(before, ViewState.Request.Restore);
    }

    private Icon LoadSourceIcon()
    {
        try
        {
            string path = _origin.IsWindow ? WindowInfo.ExecutablePath(_source) : "";
            if (!string.IsNullOrEmpty(path) && Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or ExternalException or System.ComponentModel.Win32Exception) { Diagnostics.Log.Swallowed(failure); }
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

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IReadOnlyList<PipPreset>>? Presets { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<bool?>? SoundState { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<bool, bool>? SetSound { get; set; }
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

    // Click-through needs the layered style too, or Windows still gives this window the clicks.
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

    private void ResizeTo(int longSide)
    {
        _longSide = longSide;
        Attempt(() => FitToSource());
    }

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
            TitleBarAlways = _overrides.TitleBarAlways, KeepAspectRatio = _overrides.KeepAspectRatio,
            Turns = _turn.Turns == 0 ? null : _turn.Turns, Mirrored = _turn.Mirrored ? true : null,
        };
    }

    // A saved source's view where it was, to the pixel, even over the taskbar or partly off the screen.
    internal void PlaceExactly(Rectangle bounds)
    {
        if (!Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(bounds))) return;
        Bounds = bounds;
        // Turned, it turns once capture draws and fits itself to the turned picture then: it comes back here after.
        _placeAfterTurn = _turn != _appliedTurn ? bounds : null;
    }

    private Rectangle? _placeAfterTurn;

    private Size WholeSize() => _thumbnail is null ? Size.Empty : _origin.Whole(_thumbnail.SourceSize).Size;

    internal bool KeepsUpdating => _hideItem.Checked;

    internal bool IsInteractive => _interactiveItem.Checked;

    internal void ApplyPreset(PipPreset preset)
    {
        Overrides = _overrides with
        {
            FrameRate = preset.FrameRate, OpacityPercent = preset.OpacityPercent, Scaling = preset.Scaling,
            PositionLocked = preset.PositionLocked ?? _overrides.PositionLocked,
            TitleBarAlways = preset.TitleBarAlways ?? _overrides.TitleBarAlways,
            KeepAspectRatio = preset.KeepAspectRatio ?? _overrides.KeepAspectRatio,
        };
        _crop = preset.CropFor(WholeSize());
        _zoom = PipGeometry.WholeView;
        SetTurn(preset.Turn);
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
    internal string DiagnosticSnapshot => (_thumbnail?.Snapshot() ?? "Stopped") + $"\nEnhanced hover: {_live?.HoverStatus ?? (HoverOn ? "inactive" : "off")}";

    private void ShowDiagnostics()
    {
        string text = DiagnosticSnapshot;
        new Settings.InfoWindow(T("pip.diagnostics.title"), text, (T("pip.diagnostics.copy"), () =>
        {
            try { Clipboard.SetText(text.Replace("\n", Environment.NewLine)); }
            catch (ExternalException) { Problem?.Invoke(T("errors.clipboard.busy")); }
        }, true)).ShowInFront();
    }

    // A view of the screen would otherwise show itself without end. Windows honours this from version 2004; before that
    // the view would be a black box.
    private void KeepOutOfScreenCapture()
    {
        if (_origin.IsWindow)
            return;
        if (Environment.OSVersion.Version.Build < WindowsBuild.Minimum ||
            !User32.SetWindowDisplayAffinity(Handle, User32.WDA_EXCLUDEFROMCAPTURE))
            throw new WindowOperationException(T("errors.pip.screenUnsupported"));
    }

    private void KeepPartOutOfScreenCapture(Form part)
    {
        if ((!_origin.IsWindow || _autoMoveItem.Checked) && !User32.SetWindowDisplayAffinity(part.Handle, User32.WDA_EXCLUDEFROMCAPTURE))
            throw new WindowOperationException(T("errors.pip.screenUnsupported"));
    }

    // crop is measured from the window's visible top-left corner; empty shows all of it.
    public void SetCrop(Rectangle crop)
    {
        if (crop != _crop)
            _zoom = PipGeometry.WholeView;
        _crop = crop;
        Attempt(() => FitToSource());
    }

    // Telling what is under the view needs the screen without the view on it, so while this is on the view is left out
    // of all screen capture, screenshots and sharing included.
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

    public void SetOriginalHidden(bool hidden)
    {
        ViewState.Modes before = CurrentModes();
        ViewState.Request request = hidden ? ViewState.Request.KeepHiddenOn : ViewState.Request.KeepHiddenOff;
        ViewState.Modes target = ViewState.Apply(before, request);
        if (target == before && before.Interactive) return; // Refused: direct use needs it hidden.
        ChangeInteractionMode(target.Interactive, target.Hidden);
        CheckRequest(before, request);
    }

    // Many apps, browsers first, stop drawing while covered, so a new view hides its original. Nobody asked for it, so
    // a window that can't be hidden just stays as it is, unsaid.
    internal void HideOriginalByDefault()
    {
        if (!_origin.IsWindow || _hideItem.Checked) return;
        ChangeInteractionMode(_interactiveItem.Checked, true, quiet: true);
    }

    // All mode changes converge here. Menu state is published from actual concealment, including failure and
    // recovery; an interactive view never advertises a visible original as interactive.
    private void ChangeInteractionMode(bool interactive, bool hidden, bool quiet = false, bool keepOnTaskbar = false)
    {
        _usingOriginal = false; // Any other choice about the original replaces "use it, then hide it again".
        EndLift(CoverEnd.Mode);
        EndDrop();
        _awayFrom = Guid.Empty; // And any choice replaces "hidden while you're on another desktop"
        _minimizedOriginal = false; // and "hidden while minimized".
        StepAside(false);
        if (!interactive || !hidden) EndLive();
        bool changed = SetConcealment(hidden, quiet, keepOnTaskbar);
        PublishInteractionMode(interactive && changed);
        CheckState();
    }

    private void PublishInteractionMode(bool interactive)
    {
        bool hidden = _origin.IsWindow && _origin.IsAvailable && _windows.IsConcealedBy(_source, this);
        if (!hidden)
        {
            EndLive();
            _awayFrom = Guid.Empty;
            _minimizedOriginal = false;
        }
        // Hidden only while the person is on another desktop, or because they minimized it, isn't their Keep hidden:
        // the menu, the saved setup and the state line don't show it.
        _hideItem.Checked = hidden && _awayFrom == Guid.Empty && !_minimizedOriginal;
        _interactiveItem.Checked = interactive && hidden;
        _hideItem.Enabled = !_interactiveItem.Checked;
    }

    private bool SetConcealment(bool hidden, bool quiet = false, bool keepOnTaskbar = false)
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
                // Remembered before hiding: a hidden original shows on every desktop and has none of its own.
                if (!_windows.IsConcealedBy(_source, this)) _ownDesktop = VirtualDesktops.DesktopOf(_source);
                _windows.HideOwned(_source, this);
                // Minimized by the person, it stays on the taskbar and in Alt+Tab, where they look for it.
                if (!keepOnTaskbar)
                {
                    if (SwitcherHidingStopsView) { if (!quiet) Problem?.Invoke(T("errors.switcher.windows10")); }
                    // A Store app keeps the taskbar button Windows owns; activating it shows the app (FollowSource).
                    // Only its taskbar entry goes, which Windows 11 also leaves out of Alt+Tab; a tool window's frame
                    // would replace the original's own caption buttons in the view.
                    else if (!WindowController.TaskbarButtonOwnedByWindows(_source)) _windows.RemoveTaskbarButton(_source);
                }
                // A minimized one has no picture, and restored once parked it went back to its own place on the screen,
                // unseen, where it isn't the window under the pointer that a browser looks for before it scrolls.
                // Restored now, while it can't be seen.
                if (User32.IsIconic(_source)) User32.ShowWindow(_source, User32.SW_SHOWNOACTIVATE);
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
                ReturnToOwnDesktop();
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
                catch (WindowOperationException caught)
                {
                    Diagnostics.Log.Swallowed(caught);
                }
            }

            if (quiet) Diagnostics.Log.Swallowed(failure);
            else Problem?.Invoke(failure.Message);
            return false;
        }
    }

    private Guid? _ownDesktop;

    // Shown again while another desktop is in front, Windows puts the original there; it goes back to its own instead
    // (undocumented, INV-19: where that fails it stays where Windows put it).
    private void ReturnToOwnDesktop()
    {
        Guid? own = _ownDesktop;
        _ownDesktop = null;
        if (own is not { } desktop || VirtualDesktops.Exists(desktop) == false || VirtualDesktops.CurrentDesktop() == desktop) return;
        VirtualDesktopsInternal.MoveTo(_source, desktop);
    }

    // Windows 10 can't capture a window once it is out of Alt+Tab and the taskbar; Windows 11 can.
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
        if (m.Msg == 0x0214 && !_lastShown.IsEmpty)
        {
            Rect native = Marshal.PtrToStructure<Rect>(m.LParam);
            Rectangle picture = _appliedTurn.Oriented(_lastShown);
            Rectangle bounds = PipGeometry.SnapToActualSize(Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom),
                (int)m.WParam, picture.Size, Dpi.Scale(SnapReach, DeviceDpi));
            if (_overrides.KeepAspectRatio) bounds = PipGeometry.SizeWithAspect(bounds, (int)m.WParam, picture);
            Marshal.StructureToPtr(new Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), m.LParam, false);
            m.Result = 1;
            return;
        }
        // A pointer move to the view itself: Windows has checked where the real pointer is
        // (PipInteraction.MaxRecheckWait).
        if (m.Msg == 0x0200) _live?.ViewPointerMoved(GetMessageTime());
        // Keep the modifiers carried by the press. This inactive view may process it after Ctrl is released.
        if (m.Msg is 0x0201 or 0x0203)
            _controlAtPress = (m.WParam.ToInt64() & 0x0008) != 0; // MK_CONTROL
        // Presses in quick succession don't move the pointer, and a visit starts with a move, so a right press can reach
        // the view itself between visits. It starts one and goes to the app, never to the view's menu.
        // With an administrator window in front no visit starts (BeginLive), so a left press reaches the view too: it
        // goes to the app the same way (not with Ctrl, which selects), and the app coming forward ends the blindness.
        if (m.Msg is WM_RBUTTONDOWN or WM_LBUTTONDOWN)
        {
            _pressVisit = null;
            MouseButtons button = m.Msg == WM_RBUTTONDOWN ? MouseButtons.Right : MouseButtons.Left;
            if (_live is null && _interactiveItem.Checked &&
                (button == MouseButtons.Right || (m.WParam.ToInt64() & 0x0008) == 0 && AdministratorInFront()) &&
                GivePressToVisit(ClientPointOf(m.LParam), button)) return;
        }
        if (m.Msg is WM_RBUTTONUP or WM_LBUTTONUP && _pressVisit is { } visit &&
            _pressButton == (m.Msg == WM_RBUTTONUP ? MouseButtons.Right : MouseButtons.Left))
        {
            _pressVisit = null;
            if (visit == _live) visit.TakeRelease(PointToScreen(ClientPointOf(m.LParam)), _pressButton);
            return;
        }
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

    private static Point ClientPointOf(nint lParam) => new((short)lParam.ToInt64(), (short)(lParam.ToInt64() >> 16));

    private bool GivePressToVisit(Point point, MouseButtons button)
    {
        BeginLive(point, pressed: true);
        if (_live is not { } live) return false;
        _pressVisit = live;
        _pressButton = button;
        live.TakePress(PointToScreen(point), button); // This thread got the press, so it may bring the app forward.
        return true;
    }

    // While an administrator window has the focus, Windows tells no hook of a program without those rights about the
    // pointer: a visit would see no move and end at once, over and over, and a press between visits moved the view.
    private bool AdministratorInFront()
    {
        if (ProcessInfo.SelfElevated) return false;
        nint front = User32.GetForegroundWindow();
        if (front == _frontChecked) return _frontIsAdministrator;
        _frontChecked = front;
        User32.GetWindowThreadProcessId(front, out uint process);
        _frontIsAdministrator = front != 0 && process != 0 && process != Environment.ProcessId && ProcessInfo.IsElevated(process);
        return _frontIsAdministrator;
    }

    private nint _frontChecked;
    private bool _frontIsAdministrator, _toldAdministratorInFront;

    internal bool MenuShown => _menu.Visible;

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
            ZoomAt(_appliedTurn.ToUpright(new PointF((e.X - shown.Left) / (float)shown.Width, (e.Y - shown.Top) / (float)shown.Height)),
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
        Rectangle picture = _appliedTurn.Oriented(_lastShown);
        longSide = PipGeometry.StopAtActualSize(_longSide, longSide, Math.Max(picture.Width, picture.Height) + 2 * PipGeometry.Border);
        Size size = PipGeometry.FitLongSide(longSide, picture, area.Size);
        if (step < 0 && Math.Min(size.Width, size.Height) < Dpi.Scale(PipGeometry.MinimumHeight, DeviceDpi))
            return; // Any smaller and there would be nothing left to grab.

        _longSide = Math.Max(size.Width, size.Height);
        Point? pointer = _overrides.PositionLocked ? null : PointToScreen(e.Location);
        Attempt(() => FitToSource(pointer));
    }

    // When the pointer was last on this view: a key for a view goes to the one last pointed at when nothing else says
    // which (TrayApplication.ViewFor).
    internal long PointedAt { get; private set; }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        PointedAt = Environment.TickCount64;
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
        PointedAt = Environment.TickCount64;
        if (_interactiveItem.Checked && e.Button == MouseButtons.None && !_pressed)
            BeginLive(e.Location);
        else if (_interactiveItem.Checked)
            NoVisitBecause = $"move with {e.Button}, pressed {_pressed}";
        if (_panning && e.Button == MouseButtons.Middle)
        {
            Rectangle area = PictureArea;
            SizeF moved = _appliedTurn.ToUpright(new SizeF((_panFrom.X - e.X) / (float)area.Width, (_panFrom.Y - e.Y) / (float)area.Height));
            _zoom = PipGeometry.Pan(_zoom, moved.Width * _zoom.Width, moved.Height * _zoom.Height);
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
                Rectangle part = new PipMapping(PictureArea, ShownPart(_thumbnail.SourceSize), _appliedTurn).SelectionToSource(_selection);
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

    internal void ZoomAt(PointF spot, double factor)
    {
        _zoom = PipGeometry.ZoomAround(_zoom, spot, factor);
        Attempt(() => FitToSource());
    }

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
        // Keep the recovery surface reachable if a concealed source is temporarily unresponsive. Only what this view did
        // holds it: an original that Glass, Dark or a tile hides is theirs to show, and the view closed over it before.
        bool hiddenByAnother = _origin.IsWindow && _windows.IsHidden(_source) && !_windows.IsConcealedBy(_source, this);
        if (_origin.IsWindow && _origin.IsAvailable && !hiddenByAnother &&
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
        _growing?.Dispose();
        // Teardown must never submit another draw: the source or destination HWND may already be gone.
        _selecting = false;
        if (!EndLift(CoverEnd.ViewClosed)) SettleWhenItAnswers();
        _thumbnail?.Dispose();
        _thumbnail = null;
        if ((_hideItem.Checked || _awayFrom != Guid.Empty || _minimizedOriginal) && _origin.IsAvailable)
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

    // Interactive use requires concealment; turning it off restores the prior hiding preference.
    internal void SetInteractive(bool on)
    {
        ViewState.Modes before = CurrentModes();
        ViewState.Request request = on ? ViewState.Request.InteractiveOn : ViewState.Request.InteractiveOff;
        bool hiddenBefore = _hiddenBeforeInteractive, minimizedBefore = _minimizedBeforeInteractive;
        ViewState.Modes target = ViewState.Apply(before, request, hiddenBefore, minimizedBefore);
        if (target == before) return;
        try
        {
            if (on)
            {
                _hiddenBeforeInteractive = before.Hidden;
                _minimizedBeforeInteractive = before.OriginalMinimized;
                _offTaskbarBeforeInteractive = _origin.IsWindow && _windows.IsOffTaskbar(_source);
            }
            if (target.OriginalMinimized) HideAsMinimized(backOnTaskbar: !_offTaskbarBeforeInteractive);
            else ChangeInteractionMode(target.Interactive, target.Hidden);
            if (on && _interactiveItem.Checked && _overrides.ClickThrough)
                Overrides = _overrides with { ClickThrough = false };
        }
        finally { CheckRequest(before, request, hiddenBefore, minimizedBefore); }
    }

    internal PipInteraction? LiveInteraction => _live;
    internal bool WaitsForPress => _toldAdministratorInFront && _live is null;
    // Why the pointer last moved over the view without starting a visit; for tests.
    internal string NoVisitBecause { get; private set; } = "";

    internal bool OpensOriginalOnClick => _origin.IsWindow && _hideItem.Checked && ShowsOriginalOnly;

    internal bool UsingOriginal => _usingOriginal;

    // Each change of mode is checked in every build, so tests can see it; a debug build stops at it.
    internal PipStateProblem FirstStateProblem { get; private set; }

    internal PipStateProblem StateProblem()
    {
        if (_cleaned) return PipStateProblem.None;
        return ViewState.ProblemOf(CurrentModes());
    }

    private ViewState.Modes CurrentModes() => new(Interactive: _interactiveItem.Checked, Hidden: _hideItem.Checked,
        Visiting: _live is not null, Suspended: _suspended, InputPaused: _inputPaused, UsingOriginal: _usingOriginal,
        MayInteract: CanInteract, ExperimentalSwitch: _allowInteractive,
        VisitUsesExperimental: _live is { } visit && (visit.EnhancedHover || visit.MiddleClickScroll),
        TakingDrop: _dropSince != 0, Away: _awayFrom != Guid.Empty, OriginalMinimized: _minimizedOriginal);

    internal int UnexpectedModes { get; private set; }

    // The modes after a request are what the table says, or unchanged (refused or failed, and said so). Anything else
    // is logged.
    private void CheckRequest(ViewState.Modes before, ViewState.Request request, bool hiddenBeforeInteractive = false,
        bool minimizedBeforeInteractive = false)
    {
        if (_cleaned || IsDisposed) return;
        ViewState.Modes now = CurrentModes(), expected = ViewState.Apply(before, request, hiddenBeforeInteractive, minimizedBeforeInteractive);
        // The settings and what the visit uses aren't the request's to change.
        expected = expected with { MayInteract = now.MayInteract, ExperimentalSwitch = now.ExperimentalSwitch, VisitUsesExperimental = now.VisitUsesExperimental };
        before = before with { MayInteract = now.MayInteract, ExperimentalSwitch = now.ExperimentalSwitch, VisitUsesExperimental = now.VisitUsesExperimental };
        if (now == expected || now == before) return;
        UnexpectedModes++;
        Diagnostics.Log.Event("view.unexpectedMode", ("request", request), ("interactive", now.Interactive), ("hidden", now.Hidden),
            ("visiting", now.Visiting), ("suspended", now.Suspended), ("using", now.UsingOriginal));
    }

    private void CheckState()
    {
        PipStateProblem problem = StateProblem();
        if (problem == PipStateProblem.None) return;
        if (FirstStateProblem == PipStateProblem.None)
        {
            FirstStateProblem = problem;
            Diagnostics.Log.Event("view.stateProblem", ("problem", problem));
        }
        System.Diagnostics.Debug.Fail("The view's modes don't fit together: " + problem);
    }

    internal void UseOriginal()
    {
        ViewState.Modes before = CurrentModes();
        if (!_origin.IsWindow || ViewState.Apply(before, ViewState.Request.UseOriginal) == before) return;
        bool interactive = _interactiveItem.Checked;
        GoToSource();
        if (IsDisposed || !_origin.IsAvailable || _windows.IsHidden(_source)) return;
        _usingOriginal = true;
        _usingFromInteractive = interactive;
        _originalTookFocus = User32.GetForegroundWindow() == _source;
        _originalLeftAt = 0;
        StepAside(true); // The original is in front now; the view would only cover part of it.
        CheckState();
        CheckRequest(before, ViewState.Request.UseOriginal);
    }

    // A drop can only land in the real window under the pointer, and WinScreen can't hand the data on, so the hidden
    // original comes up with the pointed-at spot under the pointer and Windows offers it the drag. The view never takes
    // the drop.
    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = DragDropEffects.None;
        AskForDrop();
    }

    // A drag that came in over the border reaches the picture later.
    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = DragDropEffects.None;
        AskForDrop();
    }

    private bool MayTakeDrop => _dropSince == 0 && _hideItem.Checked && !_usingOriginal && !_suspended && _thumbnail is not null &&
        _origin.IsWindow && _windows.IsParked(_source) && !_lastShown.IsEmpty;

    private void AskForDrop()
    {
        if (_dropAsked || !MayTakeDrop) return;
        _dropAsked = true;
        // After this call returns: the drag is in the middle of asking this window.
        BeginInvoke(() =>
        {
            _dropAsked = false;
            HandOverDrop();
        });
    }

    private bool _dropAsked;

    // Seen by the pointer rather than by drag and drop: Windows doesn't let a drag from a normal window reach an
    // elevated one, so an elevated WinScreen is never asked.
    // While Windows' drag and drop runs, the thread it started on holds the mouse with the drop's own window
    // (CLIPBRDWNDCLASS); a held button or a selection box holds it with the app's own window. That thread is the
    // pressed window's, which needn't be the one in front (File Explorer doesn't come forward for a press on a file).
    // A left button pressed elsewhere, with that drop window holding the mouse, while the pointer rests on the picture,
    // is a drag arriving; a button merely held over the view is not. A drag the view is asked about goes first.
    private void FollowDragArriving()
    {
        long now = Environment.TickCount64;
        if ((User32.GetAsyncKeyState(0x01) & 0x8000) == 0) { _heldSince = 0; _dragInSince = 0; return; }
        Point pointer = Cursor.Position;
        if (_heldSince == 0)
        {
            // Where the button went down, as near as the timer saw: on one of WinScreen's own windows (moving a view,
            // its menu, a visit's press) it isn't a drag from elsewhere.
            _heldSince = now;
            nint under = User32.WindowFromPoint(new NativePoint { X = pointer.X, Y = pointer.Y });
            User32.GetWindowThreadProcessId(under, out uint process);
            _heldFromElsewhere = under != 0 && process != (uint)Environment.ProcessId && !Bounds.Contains(pointer);
            _heldThread = under == 0 ? 0 : User32.GetWindowThreadProcessId(under, out _);
            _heldProcess = process;
            _heldClass = under == 0 ? "" : WindowInfo.ClassName(under);
            _overPictureSince = 0;
            _dragNotSeenLogged = false;
            return;
        }
        bool overPicture = _heldFromElsewhere && MayTakeDrop && RectangleToScreen(PictureArea).Contains(pointer);
        if (!overPicture || !DragRunningFor(now))
        {
            _dragInSince = 0;
            if (!overPicture) { _overPictureSince = 0; return; }
            // Held from elsewhere and resting on the picture, but no drag and drop seen: logged once per press, so a
            // report of "nothing came up" shows where it stopped.
            if (_overPictureSince == 0) _overPictureSince = now;
            else if (!_dragNotSeenLogged && now - _overPictureSince >= DragNotSeenAfter)
            {
                _dragNotSeenLogged = true;
                Diagnostics.Log.Event("view.dragNotSeen", ("program", Diagnostics.LogValue.Program(ProcessInfo.PathOf(_heldProcess))),
                    ("class", Diagnostics.LogValue.WindowClass(_heldClass)));
            }
            return;
        }
        if (_dragInSince == 0) { _dragInSince = now; return; }
        if (now - _dragInSince < DragInRests) return;
        _dragInSince = 0;
        _heldFromElsewhere = false; // Once per press.
        HandOverDrop();
    }

    private long _heldSince, _dragInSince, _overPictureSince, _processCheckedAt = -100_000;
    private bool _heldFromElsewhere, _dragNotSeenLogged, _processDragRunning;
    private uint _heldThread, _heldProcess;
    private string _heldClass = "";
    private const long DragInRests = 150, DragNotSeenAfter = 600, ProcessCheckEvery = 250;

    // On the pressed window's thread, or, every quarter second, on any thread of its program, in case it runs its drag
    // elsewhere.
    private bool DragRunningFor(long now)
    {
        if (DragAndDropRunning(_heldThread)) return true;
        if (now - _processCheckedAt >= ProcessCheckEvery)
        {
            _processCheckedAt = now;
            _processDragRunning = DragAndDropRunningInProcess(_heldProcess);
        }
        return _processDragRunning;
    }

    internal static bool DragAndDropRunningInProcess(uint process)
    {
        if (process == 0) return false;
        try
        {
            using var running = System.Diagnostics.Process.GetProcessById((int)process);
            foreach (System.Diagnostics.ProcessThread thread in running.Threads)
                using (thread)
                    if (DragAndDropRunning((uint)thread.Id)) return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { Diagnostics.Log.Swallowed(e); }
        return false;
    }

    internal static bool DragAndDropRunning(uint thread)
    {
        var info = new GuiThreadInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) && info.Capture != 0 && WindowInfo.ClassName(info.Capture) == "CLIPBRDWNDCLASS";
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretBounds;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    private void HandOverDrop()
    {
        if (IsDisposed || _dropSince != 0 || !_origin.IsAvailable || !_windows.IsParked(_source) || !DragButtonsDown()) return;
        ViewState.Modes before = CurrentModes();
        if (ViewState.Apply(before, ViewState.Request.DropStarts) == before) return;
        // Where the pointer is now: it may have gone on into the view since the drag came in at its edge.
        Point pointer = Cursor.Position;
        if (!RectangleToScreen(PictureArea).Contains(pointer)) return;
        EndLive();
        Point spot = SourceSpotAt(pointer);
        if (!User32.GetWindowRect(_source, out Rect bounds)) return;
        Rectangle at = DropPlace(pointer, spot, new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
            Screen.FromPoint(pointer).WorkingArea);
        StartGrowing();
        try { _windows.ShowParkedForDrop(_source, at.Location, holdReveal: _growing is not null); }
        catch (WindowOperationException failure)
        {
            Diagnostics.Log.Swallowed(failure);
            StopGrowing();
            return;
        }
        _dropSince = Environment.TickCount64;
        _dropReleasedAt = 0;
        _dropOutsideSince = 0;
        _dropTookFocus = false;
        _dropFromView = false;
        _foregroundBeforeDrop = User32.GetForegroundWindow();
        StepAside(true);
        CheckState();
        CheckRequest(before, ViewState.Request.DropStarts);
        Diagnostics.Log.Event("view.dropHandedOver", ("program", Diagnostics.LogValue.ProgramOf(_source)));
    }

    // Windows' own drag and drop follows the real pointer and can't be passed on as messages. In a page the same
    // press-and-move otherwise selects text, which works through the view as it is.
    private Compatibility.DragHandOver HandsOverDrags => _origin.IsWindow ? PipEnhancedHover.DragsOf(_source) : Compatibility.DragHandOver.None;

    private void PickUpDrag(Point pressed)
    {
        if (IsDisposed || _live is not { } live || _dropSince != 0 || !_origin.IsAvailable || !_windows.IsParked(_source)) return;
        ViewState.Modes modesBefore = CurrentModes();
        if (ViewState.Apply(modesBefore, ViewState.Request.DropStarts) == modesBefore) return;
        Point spot = SourceSpotAt(pressed);
        if (!User32.GetWindowRect(_source, out Rect bounds)) return;
        string item = live.PressedItem;
        nint before = _foregroundBeforeLive;
        EndLive(keepFocus: true); // The hook goes first: the press given below must reach the window.
        // The press was the hook's, so Windows doesn't know the button is down: let go meanwhile, there is nothing to
        // hand on.
        if (live.HandedOverReleased) return;
        // The original is resized below, and the view would show that.
        _pickingUp = true;
        StepAside(true);
        // Never shown: nearly see-through, it takes the press, and goes back out of sight once Windows carries the drag
        // (FollowDrop). Shown at its own size around the hand, it covered where a quick hand let go.
        const bool hold = true;
        Point pointer = Cursor.Position;
        Rectangle area = Screen.FromPoint(pointer).WorkingArea;
        Point? press = null;
        bool itemFound = false;
        long pickupStarted = Environment.TickCount64;
        try
        {
            // Never filling the screen: a maximized original, or one at the view's smaller size, lays itself out
            // differently, so the pressed item is found anew and the window moves to bring it under the pointer.
            if (item.Length > 0 && live.Identity is { } identity && _windows.NormalSize(_source) is { } normal)
            {
                Rectangle place = PickupPlace(normal, pointer, area);
                _windows.ShowParkedForDrop(_source, place.Location, place.Size, hold);
                for (long until = Environment.TickCount64 + PickupFindFor; press is null && Environment.TickCount64 < until;)
                {
                    Application.DoEvents();
                    // On its name, near the start of the row: elsewhere in a row Windows 10 starts a selection box
                    // instead.
                    if (PipControlInput.Shared.ItemBounds(identity, item, 150) is { } found &&
                        new Point(found.Left + Math.Min(28, found.Width / 2), found.Top + found.Height / 2) is var onName &&
                        place.Contains(onName))
                    {
                        // Under the hand as it is now: it goes on moving while the item is found.
                        Rectangle moved = DropPlace(Cursor.Position, new Point(onName.X - place.Left, onName.Y - place.Top), place.Size, area);
                        if (moved.Location != place.Location) _windows.ShowParkedForDrop(_source, moved.Location, place.Size, hold);
                        press = onName + new Size(moved.Left - place.Left, moved.Top - place.Top);
                    }
                }
            }
            itemFound = press is not null;
            if (press is null)
            {
                // Not found (scrolled away at the smaller size, or no name), or a page's link: at its own size with the
                // pressed spot under the pointer.
                // A window as big as the screen can't be moved to put it there: the press goes where the spot really
                // is, the pointer going there and back below, or the press misses the link.
                Rectangle at = DropPlace(Cursor.Position, spot, new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), area);
                _windows.ShowParkedForDrop(_source, at.Location, holdReveal: hold);
                press = User32.GetWindowRect(_source, out Rect shownAt) ? new Point(shownAt.Left + spot.X, shownAt.Top + spot.Y) : pointer;
            }
        }
        catch (WindowOperationException failure)
        {
            Diagnostics.Log.Swallowed(failure);
            _pickingUp = false;
            StepAside(false);
            return;
        }
        LastPickup = item.Length == 0 ? "at its own size" : $"{(itemFound ? "item found" : "item not found")} after {Environment.TickCount64 - pickupStarted} ms";
        LastPickupPress = press.Value;
        // The pressed item is under the press now: the real window gets it, and the drag is its own from here. The press
        // is made at the item and the pointer goes straight back to the hand, in one batch of input: set apart, the
        // press could land where the hand had moved to by then (a quick hand once picked up a folder beside the file).
        PressThenBack(press.Value, Cursor.Position);
        _pickingUp = false;
        _dropSince = Environment.TickCount64;
        _dropReleasedAt = 0;
        _dropOutsideSince = 0;
        _dropTookFocus = false;
        _dropFromView = true;
        _foregroundBeforeDrop = before;
        StepAside(true);
        CheckState();
        CheckRequest(modesBefore, ViewState.Request.DropStarts);
        DragsPickedUp++;
        Diagnostics.Log.Event("view.dragPickedUp", ("program", Diagnostics.LogValue.ProgramOf(_source)));
    }

    private void StartGrowing()
    {
        StopGrowing();
        // It would grow a turned picture into an upright window.
        if (!_appliedTurn.IsUpright) return;
        // Only a softening: whatever goes wrong taking the picture, the window comes up as before, at once.
        try { _growing = GrowingPicture.Start(RectangleToScreen(PictureArea), ShownForDropBounds, RevealForDrop); }
        catch (Exception failure) when (failure is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            Diagnostics.Log.Swallowed(failure);
            _growing = null;
        }
        if (_growing is not null) GrowsStarted++;
    }

    private void StopGrowing()
    {
        _growing?.Dispose();
        _growing = null;
    }

    // Asked on the picture's thread.
    private Rectangle? ShownForDropBounds() =>
        _windows.IsShownForDrop(_source) && User32.GetWindowRect(_source, out Rect r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : null;

    private void RevealForDrop()
    {
        try { _windows.RevealShownForDrop(_source); }
        catch (WindowOperationException failure) { Diagnostics.Log.Swallowed(failure); }
    }

    private GrowingPicture? _growing;

    internal int GrowsStarted { get; private set; }

    private const long PickupFindFor = 600;
    private static readonly Size SmallestPickup = new(420, 300);

    // At most three quarters of its screen each way, so there is room left to drag to.
    internal static Rectangle PickupPlace(Size normal, Point pointer, Rectangle area)
    {
        int width = Math.Min(Math.Max(Math.Min(normal.Width, area.Width * 3 / 4), SmallestPickup.Width), area.Width);
        int height = Math.Min(Math.Max(Math.Min(normal.Height, area.Height * 3 / 4), SmallestPickup.Height), area.Height);
        int x = Math.Clamp(pointer.X - width / 2, area.Left, area.Right - width);
        int y = Math.Clamp(pointer.Y - height / 2, area.Top, area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    internal string LastPickup { get; private set; } = "";

    internal Point LastPickupPress { get; private set; }

    internal int DragsPickedUp { get; private set; }

    private bool _pickingUp;

    private static void PressThenBack(Point press, Point hand)
    {
        const uint Move = 0x0001, LeftDown = 0x0002, Absolute = 0x8000, VirtualDesk = 0x4000;
        Rectangle screen = SystemInformation.VirtualScreen;
        MouseInput At(Point p, uint flags) => new()
        {
            X = (int)((p.X - screen.Left + 0.5) * 65536 / screen.Width),
            Y = (int)((p.Y - screen.Top + 0.5) * 65536 / screen.Height),
            Flags = flags | Move | Absolute | VirtualDesk,
        };
        Input[] inputs = press == hand ? [new() { Type = 0, Mouse = At(press, LeftDown) }]
            : [new() { Type = 0, Mouse = At(press, LeftDown) }, new() { Type = 0, Mouse = At(hand, 0) }];
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Input>());
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public MouseInput Mouse;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint Extra;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    internal Point SourceSpotAt(Point screen) => new PipMapping(PictureArea, _lastShown, _appliedTurn).ToSource(PointToClient(screen));

    internal static Rectangle DropPlace(Point pointer, Point spot, Size size, Rectangle area)
    {
        int x = pointer.X - spot.X, y = pointer.Y - spot.Y;
        x = size.Width >= area.Width ? area.Left : Math.Clamp(x, area.Left, area.Right - size.Width);
        y = size.Height >= area.Height ? area.Top : Math.Clamp(y, area.Top, area.Bottom - size.Height);
        return new Rectangle(new Point(x, y), size);
    }

    // The drag is over once both buttons are up. A drop that opened something of its own (a "Replace or skip?" dialog,
    // a menu) keeps it out until the focus goes elsewhere; the original itself taking the focus doesn't (a browser
    // comes forward for any drop).
    // A drag that only passes over, leaving the original with a button still down, hides it at once, so it isn't in the
    // way of where the drag is going.
    private void FollowDrop()
    {
        if (_dropSince == 0) return;
        if (!_origin.IsAvailable || !_windows.IsShownForDrop(_source)) { EndDrop(); return; }
        long now = Environment.TickCount64;
        if (DragButtonsDown())
        {
            // Taken from the view: once Windows carries the drag, the original goes back out of sight at once, so it
            // never covers where the drag is going. (So it can't be dropped back into the same window; the view, too,
            // stays out of the way until the drag ends.)
            if (_dropFromView && DragAndDropRunning(User32.GetWindowThreadProcessId(_source, out _)))
            {
                EndDrop(keepFocus: true);
                return;
            }
            Point pointer = Cursor.Position;
            bool inside = User32.GetWindowRect(_source, out Rect r) &&
                Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(pointer);
            // Its own drag, carried over another view: the original, at its own size, may cover that view, which is
            // where the drag is going (its view hands its own original over).
            if (_dropFromView && inside && Application.OpenForms.OfType<PipWindow>().Any(other =>
                    other != this && other.Visible && other.Bounds.Contains(pointer)))
            {
                EndDrop(keepFocus: true);
                return;
            }
            if (inside) _dropOutsideSince = 0;
            else if (_dropOutsideSince == 0) _dropOutsideSince = now;
            // Out of the way of where the drag is going. Its own drag goes on without it in sight (Windows carries it),
            // and the focus stays where it is: moving it can cancel a drag.
            else if (now - _dropOutsideSince >= DropPassedBy) EndDrop(keepFocus: _dropFromView);
            return;
        }
        if (_dropReleasedAt == 0) _dropReleasedAt = now;
        nint foreground = User32.GetForegroundWindow();
        User32.GetWindowThreadProcessId(_source, out uint process);
        if (foreground != 0 && foreground != _source && SourcePopups.IsPopupOf(foreground, _source, process))
        {
            _dropTookFocus = true;
            _dropReleasedAt = now;
            return;
        }
        if (now - _dropReleasedAt < (_dropTookFocus ? UseOriginalGrace : DropSettles)) return;
        EndDrop();
    }

    // Safe to call at any time. Focus the original had goes back to the window before the drag (or the view): keys
    // mustn't go to a window that can't be seen.
    private void EndDrop(bool keepFocus = false)
    {
        if (_dropSince == 0) return;
        ViewState.Modes before = CurrentModes();
        _dropSince = 0;
        StopGrowing();
        try { _windows.ReturnToParking(_source); }
        catch (WindowOperationException failure) { Diagnostics.Log.Swallowed(failure); }
        if (keepFocus) _focusBackAfterDrag = true;
        if (!_usingOriginal)
        {
            // Still dragging (the original went back as the drag went on elsewhere): the view stays out of the way
            // until the drag is dropped or given up.
            if (DragButtonsDown()) _backAfterRelease = true;
            else StepAside(false);
            if (!keepFocus && User32.GetForegroundWindow() == _source)
            {
                nint back = _foregroundBeforeDrop != 0 && _foregroundBeforeDrop != _source && User32.IsWindow(_foregroundBeforeDrop) &&
                            !_windows.IsHidden(_foregroundBeforeDrop) ? _foregroundBeforeDrop : Handle;
                User32.SetForegroundWindow(back);
            }
        }
        CheckRequest(before, ViewState.Request.DropEnds);
        Diagnostics.Log.Event("view.dropEnded", ("tookFocus", _dropTookFocus));
    }

    private static bool DragButtonsDown() =>
        (User32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (User32.GetAsyncKeyState(0x02) & 0x8000) != 0;

    private long _dropSince, _dropReleasedAt, _dropOutsideSince;
    private bool _dropTookFocus, _dropFromView, _focusBackAfterDrag, _backAfterRelease;

    private void FollowReleaseAfterDrag()
    {
        if (!_backAfterRelease || DragButtonsDown() || _dropSince != 0) return;
        _backAfterRelease = false;
        if (!_usingOriginal && !_suspended) StepAside(false);
    }

    // Keys mustn't stay with a window that can't be seen once the drag is let go.
    private void FollowFocusAfterDrag()
    {
        if (!_focusBackAfterDrag || DragButtonsDown()) return;
        _focusBackAfterDrag = false;
        if (_dropSince != 0 || User32.GetForegroundWindow() != _source || !_windows.IsHidden(_source)) return;
        nint back = _foregroundBeforeDrop != 0 && _foregroundBeforeDrop != _source && User32.IsWindow(_foregroundBeforeDrop) &&
                    !_windows.IsHidden(_foregroundBeforeDrop) ? _foregroundBeforeDrop : Handle;
        User32.SetForegroundWindow(back);
    }
    private nint _foregroundBeforeDrop;
    private const long DropSettles = 500, DropPassedBy = 300;

    internal bool HandingOverDrop => _dropSince != 0;

    private nint[] _steppedAside = [];

    private void StepAside(bool aside)
    {
        if (aside)
        {
            if (_steppedAside.Length > 0 || !IsHandleCreated) return;
            _steppedAside = [.. OwnedForms.Where(part => part.IsHandleCreated && User32.IsWindowVisible(part.Handle)).Select(part => part.Handle), Handle];
            foreach (nint window in _steppedAside) User32.ShowWindow(window, User32.SW_HIDE);
            return;
        }
        if (_steppedAside.Length == 0) return;
        nint[] back = _steppedAside;
        _steppedAside = [];
        foreach (nint window in back.Reverse())
            if (User32.IsWindow(window)) User32.ShowWindow(window, User32.SW_SHOWNOACTIVATE);
        StayOnTop();
    }

    internal bool SteppedAside => _steppedAside.Length > 0;

    // Its own dialogs and menus count as still using it; a moment with no window in front is only passing.
    private void FollowOriginalInUse()
    {
        if (!_usingOriginal) return;
        if (_suspended || _thumbnail is null || _windows.IsHidden(_source)) { _usingOriginal = false; StepAside(false); return; }
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
        bool interactive = _usingFromInteractive && CanInteract;
        ChangeInteractionMode(interactive, true); // Minimized while in use, it is restored as it is hidden.
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

    // Under the taskbar a browser or Electron app finds itself covered, stops drawing and taking pointer moves, and
    // doesn't look again until clicked; moving it once Enhanced hover is turned on, or at the start of a visit, is too
    // late.
    private bool HoverPlacementWanted => PipEnhancedHover.Supported && PipEnhancedHover.Describe(_source) is var (app, native) && native &&
        (PipHoverApps.SideEdgeAlways(app) || (app != PipHoverApp.None && HoverOn));

    private bool AppScrollsWithHover => PipEnhancedHover.Supported && PipEnhancedHover.Describe(_source) is var (app, native) && native &&
        PipHoverApps.ScrollsWithHover(app);

    private void PlaceForHover()
    {
        if (!_origin.IsWindow || !_windows.IsParked(_source)) return;
        try { _windows.SetParkedHoverSurface(_source, HoverPlacementWanted); }
        catch (WindowOperationException failure) { Problem?.Invoke(failure.Message); }
    }

    internal void SetMiddleClickScroll(bool enabled)
    {
        _middleScrollItem.Checked = enabled;
        if (_live is { } live) { live.MiddleClickScroll = MiddleScrollOn; if (!MiddleScrollOn) live.CancelScroll(); }
    }

    internal string LastVisitEnd { get; private set; } = "";
    private string? _endReason; // Said by whatever ends the visit; without it, the caller is named.

    internal Rectangle? TitleBarBounds => _titleBar is { Visible: true } bar ? bar.Bounds : null;

    internal IReadOnlyCollection<PressOutcome> PressOutcomes => _pressOutcomes;

    internal void ForgetPresses()
    {
        _pressOutcomes.Clear();
        _live?.ForgetPresses();
    }

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

    internal bool Frozen => _frozen && _thumbnail?.CanFreeze == true;

    internal void SetFrozen(bool on)
    {
        if (on && _thumbnail?.CanFreeze != true) return;
        _frozen = on;
        _thumbnail?.SetFrozen(on);
        if (on) EndLive();
        Diagnostics.Log.Event("view.frozen", ("on", on));
        CheckState();
    }

    // What was asked for; capture draws it, Windows' thumbnail can't, so the view shows upright meanwhile.
    internal ViewTurn RequestedTurn => _turn;
    internal ViewTurn Turn => _appliedTurn;
    internal bool CanTurn => _thumbnail?.CanFreeze == true;

    internal void SetTurn(ViewTurn turn)
    {
        if (turn == _turn) return;
        _placeAfterTurn = null;
        _turn = turn;
        Diagnostics.Log.Event("view.turned", ("turns", turn.Turns), ("mirrored", turn.Mirrored));
        FollowTurn();
    }

    private void FollowTurn()
    {
        if (_thumbnail is null) return;
        ViewTurn turn = CanTurn ? _turn : ViewTurn.Upright;
        if (turn == _appliedTurn) return;
        EndLive(); // Its mapping was for the old turn; the next move starts a new visit.
        _appliedTurn = turn;
        _thumbnail.SetTurn(turn);
        // Turned a quarter, the view swaps its sides about its middle.
        Point? middle = _overrides.PositionLocked ? null : new Point(Left + Width / 2, Top + Height / 2);
        Attempt(() => FitToSource(middle));
        if (_placeAfterTurn is { } exact && turn == _turn)
        {
            _placeAfterTurn = null;
            Bounds = exact;
        }
    }

    private void BeginLive(Point pointer, bool pressed = false)
    {
        // The table first: used directly, hidden, on the screen, not paused, no visit already (and experimental help
        // only with the switch). Then what only the view knows: a picture, the window there, no menu or block.
        ViewState.Modes before = CurrentModes() with { VisitUsesExperimental = HoverOn || MiddleScrollOn };
        if (ViewState.Apply(before, ViewState.Request.VisitStarts) == before) { NoVisitBecause = "state " + before; return; }
        // A frozen picture isn't what the window shows now, so presses on it would land somewhere else.
        if (_thumbnail is null || !_origin.IsAvailable || !_windows.IsHidden(_source) ||
            _menu.Visible || TopLayer.InputBlocked || ShowsOriginalOnly || Frozen)
        {
            NoVisitBecause = $"picture {_thumbnail is not null}, available {_origin.IsAvailable}, hidden {_windows.IsHidden(_source)}, menu {_menu.Visible}, " +
                $"blocked {TopLayer.InputBlocked}, original only {ShowsOriginalOnly}, frozen {Frozen}";
            return;
        }
        Rectangle area = PictureArea;
        if (!area.Contains(pointer)) { NoVisitBecause = $"{pointer} outside {area}"; return; }
        bool blind = AdministratorInFront();
        if (blind != _toldAdministratorInFront)
        {
            _toldAdministratorInFront = blind;
            if (blind) Diagnostics.Log.Event("view.visitWaitsForPress", ("front", Diagnostics.LogValue.ProgramOf(User32.GetForegroundWindow())));
        }
        if (blind && !pressed) { NoVisitBecause = "an administrator window in front"; return; }
        NoVisitBecause = "";
        _foregroundBeforeLive = User32.GetForegroundWindow();
        _liveSawSourceFocus = false;
        _unobservedSince = 0;
        _popups ??= CreateSourcePopups();
        var live = new PipInteraction(_source, LiveMapping(area), Handle, _popups);
        // Both stay experimental, also where the view itself may be used without the switch.
        live.MiddleClickScroll = MiddleScrollOn;
        live.AppScrollsWithHover = AppScrollsWithHover;
        live.EnhancedHover = HoverOn;
        live.Left += () => BeginInvokeIfAlive(() => { _endReason = "the pointer left"; EndLive(); });
        live.Pressed += _pressOutcomes.Enqueue;
        live.Answered += outcome => BeginInvokeIfAlive(() => OnAnswered(outcome));
        live.ViewDragged += moved => BeginInvokeIfAlive(() => DragLiveView(moved));
        live.CaptionPressed += action => BeginInvokeIfAlive(() => OnCaption(action));
        live.QuestionShown += () => BeginInvokeIfAlive(OnQuestion);
        live.HandsOverDrags = HandsOverDrags;
        live.DragPickedUp += pressed => BeginInvokeIfAlive(() => PickUpDrag(pressed));
        _liveStartedZoomed = User32.IsZoomed(_source) || CoversAMonitor(_source);
        try
        {
            _windows.Park(_source);
            _windows.SetParkedHoverSurface(_source, HoverPlacementWanted);
            live.Start(PointToScreen(pointer));
            _live = live;
            _liveStartedAt = Environment.TickCount64;
            _windows.SetParkedReachable(_source, true);
            _cursorFromFrame = true; // Whatever shape the pointer arrived with goes back to the arrow once.
            CheckState();
            CheckRequest(before, ViewState.Request.VisitStarts);
        }
        catch (WindowOperationException failure)
        {
            _live = null;
            live.Dispose();
            try { _windows.SetParkedReachable(_source, false); _windows.SetParkedHoverSurface(_source, false); } catch (WindowOperationException caught) { Diagnostics.Log.Swallowed(caught); }
            SetInputPassesThrough(false);
            _interactiveItem.Checked = false;
            Problem?.Invoke(failure.Message);
        }
    }

    private void BeginInvokeIfAlive(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException caught) when (IsDisposed || !IsHandleCreated) { Diagnostics.Log.Swallowed(caught); }
    }

    private void DragLiveView(Size moved)
    {
        if (_live is null || _overrides.PositionLocked)
            return;
        Location += moved;
        if (_thumbnail is not null)
            _live.Update(LiveMapping(PictureArea));
    }

    // In this order: the source stops taking clicks, goes home, the hook goes, the view takes input, keys come back.
    // keepFocus: a drag handed to the original keeps the focus it has, or the drag would be cancelled.
    private void EndLive(bool keepFocus = false, [System.Runtime.CompilerServices.CallerMemberName] string caller = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        ClaimEscape(_selecting); // Scrolling must not keep a global Escape claim after a visit/suspension ends.
        string? reason = _endReason;
        _endReason = null;
        if (_live is not { } live)
            return;
        LastVisitEnd = reason ?? $"{caller} ({line})";
        ViewState.Modes before = CurrentModes();
        _live = null;
        _liveEndedAt = Environment.TickCount64;
        if (_liveEndedAt - _liveStartedAt < 300 && Bounds.Contains(Cursor.Position))
            Diagnostics.Suspicion.VisitsEndAtOnce.Note(("front", Diagnostics.LogValue.ProgramOf(User32.GetForegroundWindow())),
                ("frontClass", Diagnostics.LogValue.ClassOf(User32.GetForegroundWindow())));
        CheckRequest(before, ViewState.Request.VisitEnds);
        try { if (_origin.IsAvailable) { _windows.SetParkedReachable(_source, false); _windows.SetParkedHoverSurface(_source, HoverPlacementWanted); } }
        catch (WindowOperationException failure) { Problem?.Invoke(failure.Message); }
        live.Dispose();
        Cursor.Current = Cursors.Default;
        SetInputPassesThrough(false);
        if (!keepFocus && User32.GetForegroundWindow() == _source)
        {
            // Not to a window WinScreen keeps hidden, such as another view's original: put in front, that counts as the
            // person going to it, and it comes out of its view.
            nint back = _foregroundBeforeLive != 0 && _foregroundBeforeLive != _source && User32.IsWindow(_foregroundBeforeLive) &&
                        !_windows.IsHidden(_foregroundBeforeLive) ? _foregroundBeforeLive : Handle;
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

    // The view doesn't take the focus, so Esc can't reach it the usual way. Claimed only during a crop drag or optional
    // scrolling: a pointer merely resting here mustn't take Esc from another app.
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

    // A hidden original just went above other topmost windows too.
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
        CheckState();
    }

    // Windows lifts an activated always-on-top window over the taskbar; this puts it back under.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (IsHandleCreated) StayOnTop();
    }

    internal void EndVisit() => EndLive();

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

    // A question nobody can see mustn't wait, so the original is shown.
    private void OnQuestion()
    {
        _endReason = "the original asked something";
        EndLive();
        if (_origin.IsAvailable) GoToSource();
    }

    // Minimizing minimizes the view instead (a minimized window has no picture), and maximizing goes to the original.
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

    // Minimized while shown, an original stopped drawing and its view stayed on the last picture: it
    // goes behind its view instead and goes on drawing. One that can't be hidden stays minimized.
    // With KeepMinimizedOriginalsOnTaskbar it keeps its taskbar button and Alt+Tab entry, as a minimized window does,
    // and picking it there shows it (ViewState.Facts.Minimized); otherwise Keep hidden goes on.
    private void FollowOriginalMinimized()
    {
        bool minimized = _origin.IsWindow && User32.IsIconic(_source);
        bool justMinimized = minimized && !_sourceWasMinimized;
        _sourceWasMinimized = minimized;
        if (!justMinimized || _hideItem.Checked || _suspended || _usingOriginal || _awayFrom != Guid.Empty ||
            _dropSince != 0 || !_origin.IsAvailable) return;
        ViewState.Modes before = CurrentModes();
        if (_minimizedOriginal)
        {
            // Its own button clicked while Windows still had it in front: that minimizes it, but it already looked
            // minimized, so the person meant to bring it back.
            GoToSource();
            return;
        }
        if (_viewPreferences.KeepMinimizedOriginalsOnTaskbar)
        {
            if (ViewState.Apply(before, ViewState.Request.OriginalMinimized) == before) return;
            HideAsMinimized(backOnTaskbar: false);
            CheckRequest(before, ViewState.Request.OriginalMinimized);
            Diagnostics.Log.Event("view.minimizedOriginalKept", ("program", Diagnostics.LogValue.ProgramOf(_source)), ("hidden", _minimizedOriginal));
            return;
        }
        // Used directly before it was shown, it is again.
        ViewState.Request request = _interactiveBeforeShown && CanInteract ? ViewState.Request.InteractiveOn : ViewState.Request.KeepHiddenOn;
        ViewState.Modes target = ViewState.Apply(before, request);
        ChangeInteractionMode(target.Interactive, target.Hidden, quiet: true);
        CheckRequest(before, request);
        Diagnostics.Log.Event("view.minimizedOriginalHidden", ("program", Diagnostics.LogValue.ProgramOf(_source)), ("hidden", _hideItem.Checked));
    }

    // Hidden behind the view, but on the taskbar and in Alt+Tab. Coming back from direct use, which took it off them,
    // it goes back on unless it was off them before.
    private void HideAsMinimized(bool backOnTaskbar)
    {
        ChangeInteractionMode(false, true, quiet: true, keepOnTaskbar: true);
        if (!_windows.IsConcealedBy(_source, this)) return;
        if (backOnTaskbar)
        {
            try { _windows.PutBackOnTaskbarWhileHidden(_source); }
            catch (WindowOperationException failure) { Diagnostics.Log.Swallowed(failure); }
        }
        _minimizedOriginal = true;
        PublishInteractionMode(false);
        CheckState();
    }

    // Already minimized when the view opens counts as nothing new.
    private bool _sourceWasMinimized = true, _interactiveBeforeShown;

    private static bool CoversAMonitor(nint window) =>
        User32.GetWindowRect(window, out Rect r) &&
        WindowController.FillsAMonitor(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), [.. Screen.AllScreens.Select(s => s.Bounds)]);

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

    // A minimized window has nothing to measure, so it keeps its last shape. The crop itself is left alone, so it comes
    // back whole when a source that shrank past it grows again.
    private Rectangle ShownPart(Size sourceSize) => PipGeometry.ZoomedPart(CroppedPart(sourceSize), _zoom);

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
        Size size = PipGeometry.FitLongSide(_longSide, _appliedTurn.Oriented(shown), area.Size);
        var wanted = new Rectangle(area.Right - size.Width - margin, area.Bottom - size.Height - margin,
            size.Width, size.Height);
        var others = Application.OpenForms.OfType<PipWindow>().Where(other => other != this && other.Visible)
            .Select(other => other.Bounds);
        Bounds = new Rectangle(PipGeometry.FreeSpot(wanted, others, area, margin), size);
        _lastShown = shown;
        view.SetViewport(PictureArea, shown);
    }

    private void FitToSource(Point? pointer = null)
    {
        if (_thumbnail is null)
            return;

        Rectangle shown = ShownPart(_thumbnail.SourceSize);
        if (shown.Width <= 0 || shown.Height <= 0)
            throw new WindowOperationException(T("errors.pip.noSurface"));

        Rectangle area = Screen.FromControl(this).WorkingArea;
        Size size = PipGeometry.FitLongSide(_longSide, _appliedTurn.Oriented(CroppedPart(_thumbnail.SourceSize)), area.Size);
        Point place = pointer is { } spot
            ? PipGeometry.KeepUnderPointer(Bounds, size, spot)
            : PipGeometry.KeepCorner(Bounds, size, area);
        // Made smaller only to fit the screen (a quarter turn of a wide view), it keeps the size it was given, so it
        // has it again once it fits.
        _fitting = true;
        try { Bounds = new Rectangle(PipGeometry.KeepInside(new Rectangle(place, size), area), size); }
        finally { _fitting = false; }
        _lastShown = shown;
        _thumbnail.SetViewport(PictureArea, shown);
    }

    // A hidden original that comes to the front is shown only when the person picked it (its taskbar button or
    // thumbnail, the switcher); anything else passes the focus on and keeps it hidden. The answers are one table,
    // ViewState.OriginalArrived (INV-20). A lifted original (Keep hidden off, covered) goes to LiftedArrived (INV-22).
    private void RevealIfActivatedElsewhere()
    {
        Guid? desktop = VirtualDesktops.CurrentDesktop();
        if (desktop is not null && desktop != _seenDesktop)
        {
            if (_seenDesktop is not null) _desktopSwitchedAt = Environment.TickCount64;
            _seenDesktop = desktop;
        }
        AltKeyWatch.Start();
        nint foreground = User32.GetForegroundWindow();
        // Held now, or pressed since the last look: a quick Alt+Tab can fall between two ticks (Windows 10).
        if ((User32.GetAsyncKeyState(0x12 /* Alt */) & 0x8001) != 0) _altSeenAt = Environment.TickCount64;
        bool focused = _origin.IsWindow && foreground == _source;
        bool arrived = focused && !_sourceWasFocused;
        _sourceWasFocused = focused;
        nint before = _foregroundBefore;
        if (!focused) _foregroundBefore = foreground;
        long now = Environment.TickCount64;
        if (!arrived) return;
        if (_over is not null)
        {
            LiftedArrived(before);
            return;
        }
        var facts = new ViewState.Facts(Hidden: _hideItem.Checked, Away: _awayFrom != Guid.Empty, Visiting: _live is not null,
            // The focus coming back from a visit, not the hand-off of this view being minimized since (that came within
            // half a second of the visit ending in a Windows 10 VM).
            JustAfterVisit: now - _liveEndedAt < RevealGrace && !(_suspended && _suspendedAt >= _liveEndedAt),
            Picked: PickedFromTaskbarOrSwitcher(before),
            JustAfterDesktopSwitch: _desktopSwitchedAt != 0 && now - _desktopSwitchedAt < SwitchFocusGrace,
            Minimized: _minimizedOriginal);
        ViewState.Arrival answer = ViewState.OriginalArrived(facts);
        LastArrival = $"{answer}: before {WindowInfo.ClassName(before)}, picked {facts.Picked} (Alt {now - Math.Max(_altSeenAt, AltKeyWatch.LastAltAt)} ms ago, " +
            $"off the taskbar {_windows.IsOffTaskbar(_source)}, tool window {(User32.GetWindowLongPtr(_source, User32.GWL_EXSTYLE) & User32.WS_EX_TOOLWINDOW) != 0})";
        switch (answer)
        {
            case ViewState.Arrival.PassFocusOn:
                // Not picked by the person: Windows passed the focus on (a window minimized or closed, a menu or panel
                // gone, this view minimized) or an app brought itself forward, and a hidden original, lifted above the
                // rest, was first in line. It stays hidden; the focus goes where it would have gone without it.
                nint next = NextWindowForFocus();
                User32.SetForegroundWindow(next != 0 ? next : User32.GetShellWindow());
                Diagnostics.Log.Event("view.handOffKept", ("program", Diagnostics.LogValue.ProgramOf(_source)),
                    ("before", Diagnostics.LogValue.ClassOf(before)));
                break;
            case ViewState.Arrival.ViewTakesFocus:
                // A hidden original is on every desktop, so on switching to one with nothing else on it Windows hands
                // it the focus. Keep hidden, Interactive and "hidden while away" stay as they were.
                User32.SetForegroundWindow(Handle);
                Diagnostics.Log.Event("view.switchFocusKept", ("program", Diagnostics.LogValue.ProgramOf(_source)));
                break;
            case ViewState.Arrival.Show:
                LastReveal = now;
                // What brought it forward: the window before it, and whether this view was minimized.
                Diagnostics.Log.Event("view.revealed", ("program", Diagnostics.LogValue.ProgramOf(_source)),
                    ("before", Diagnostics.LogValue.ClassOf(before)), ("minimized", _suspended));
                if (_suspended) ResumeView();
                GoToSource();
                break;
        }
    }

    internal string LastArrival { get; private set; } = "";

    private nint _foregroundBefore;

    // A window off the taskbar is out of the switcher too, on Windows 10 and 11. Store apps keep the button Windows
    // owns, so choosing it shows them.
    private bool PickedFromTaskbarOrSwitcher(nint before)
    {
        // A quick Alt+Tab switches before the switcher shows, so it is never in front: Alt held a moment ago counts.
        // Windows 10 hands the focus on through its staging window, which can take longer; without Alt it may be a
        // desktop switch, which isn't a pick.
        string beforeClass = before != 0 ? WindowInfo.ClassName(before) : "";
        long sinceAlt = Environment.TickCount64 - Math.Max(_altSeenAt, AltKeyWatch.LastAltAt);
        bool switcher = SwitcherClasses.Contains(beforeClass) || sinceAlt < AltSwitchGrace ||
                        beforeClass == "ForegroundStaging" && sinceAlt < AltStagingGrace;
        Point pointer = Cursor.Position;
        nint under = User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = pointer.X, Y = pointer.Y }), User32.GA_ROOT);
        bool taskbar = under != 0 && TaskbarClasses.Contains(WindowInfo.ClassName(under));
        if (!switcher && !taskbar) return false;
        return (User32.GetWindowLongPtr(_source, User32.GWL_EXSTYLE) & User32.WS_EX_TOOLWINDOW) == 0 && !_windows.IsOffTaskbar(_source);
    }

    private long _altSeenAt = long.MinValue / 2;
    private const long AltSwitchGrace = 400, AltStagingGrace = 2000;

    private static readonly HashSet<string> TaskbarClasses = new(StringComparer.Ordinal)
        { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow" };

    private static readonly HashSet<string> SwitcherClasses = new(StringComparer.Ordinal)
        { "MultitaskingViewFrame", "XamlExplorerHostIslandWindow", "TaskSwitcherWnd" };

    // The highest window that is shown, can be used and isn't hidden by WinScreen; 0 when none (the desktop takes it).
    private nint NextWindowForFocus()
    {
        const uint GW_CHILD = 5, GW_HWNDNEXT = 2;
        nint window = User32.GetWindow(User32.GetDesktopWindow(), GW_CHILD);
        for (int seen = 0; window != 0 && seen < 4096; seen++, window = User32.GetWindow(window, GW_HWNDNEXT))
        {
            const long WS_DISABLED = 0x08000000;
            if (window == _source || window == Handle || !User32.IsWindowVisible(window) || User32.IsIconic(window) ||
                (User32.GetWindowLongPtr(window, User32.GWL_STYLE) & WS_DISABLED) != 0 ||
                // Nearly invisible windows, such as a browser's own helpers, never take the focus: given it, Edge put
                // itself back at its parked place on leaving full screen. A window Glass or Dark draws again does.
                (_windows.IsHidden(window) || WindowInfo.IsConcealed(window)) && !_windows.IsDrawnByEffect(window))
                continue;
            long style = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
            if ((style & (User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW)) != 0) continue;
            if (Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (!User32.GetWindowRect(window, out Rect bounds) || bounds.Right - bounds.Left < 8 || bounds.Bottom - bounds.Top < 8) continue;
            return window;
        }
        return 0;
    }

    private Guid _awayFrom;
    private bool _minimizedOriginal;
    private int _desktopTick;

    internal bool HiddenWhileMinimized => _minimizedOriginal;

    private Guid? _seenDesktop;
    private long _desktopSwitchedAt;
    private const long SwitchFocusGrace = 1000;

    internal bool HiddenWhileAway => _awayFrom != Guid.Empty;

    // Many apps stop drawing while their desktop isn't shown, which freezes the view: while the person is elsewhere the
    // original is hidden for now, and comes back on its desktop.
    private void FollowDesktops()
    {
        bool away = _awayFrom != Guid.Empty;
        // Hidden already by something else (another view, a look) is left to it.
        // Not while Go to it is taking the person to the window's desktop: until that switch is over the window is on
        // another desktop, and hiding it then would cancel the switch.
        bool mayHide = !away && _goingToUntil == 0 && _origin.IsWindow && _origin.IsAvailable && !_hideItem.Checked && !_windows.IsHidden(_source) && !_usingOriginal && _live is null &&
            _thumbnail is not null && !SwitcherHidingStopsView && !User32.IsIconic(_source);
        bool here = away || !mayHide || VirtualDesktops.OnCurrentDesktop(_source);
        if (!away && here) return; // The usual case costs one question.
        AwayFromDesktop.Step step = AwayFromDesktop.Decide(_awayFrom, mayHide, here,
            away ? null : VirtualDesktops.DesktopOf(_source), VirtualDesktops.CurrentDesktop(), away ? VirtualDesktops.Exists(_awayFrom) : null);
        ViewState.Modes before = CurrentModes();
        if (step == AwayFromDesktop.Step.Hide)
        {
            if (ViewState.Apply(before, ViewState.Request.LeftDesktop) == before) return;
            Guid desktop = VirtualDesktops.DesktopOf(_source) ?? Guid.Empty;
            ChangeInteractionMode(false, true);
            if (!_windows.IsConcealedBy(_source, this)) return;
            _awayFrom = desktop;
            PublishInteractionMode(false);
            CheckState();
            CheckRequest(before, ViewState.Request.LeftDesktop);
            Diagnostics.Log.Event("view.awayHidden", ("program", Diagnostics.LogValue.ProgramOf(_source)));
        }
        else if (step == AwayFromDesktop.Step.ShowAgain)
        {
            ChangeInteractionMode(false, false);
            CheckRequest(before, ViewState.Request.BackToDesktop);
            Diagnostics.Log.Event("view.awayShown", ("program", Diagnostics.LogValue.ProgramOf(_source)));
        }
    }

    private bool _sourceWasFocused;
    private long _liveStartedAt, _liveEndedAt, _suspendedAt;
    private PipInteraction? _pressVisit; // Its release may reach the view too, and goes to the same visit.
    private MouseButtons _pressButton;
    private const long RevealGrace = 500;

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
        FollowReleaseAfterDrag();
        FollowFocusAfterDrag();
        if (_dropSince == 0) RevealIfActivatedElsewhere();
        else _sourceWasFocused = User32.GetForegroundWindow() == _source; // A drop may focus it; that isn't asking for it.
        FollowDragArriving();
        FollowDrop();
        FollowOriginalInUse();
        FinishGoingTo();
        if (_suspended && _over is not null) FollowLifted(); // A settle the original didn't answer, tried again.
        if (_suspended) return;
        if (++_desktopTick % 5 == 0) FollowDesktops();
        if (IsDisposed) return;

        try
        {
            _thumbnail?.Poll(ClientSize);
            FollowTurn();
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
        try { _windows.FollowParkedPlace(_source); } catch (WindowOperationException caught) { Diagnostics.Log.Swallowed(caught); }
        PauseInput(_menu.Visible || TopLayer.InputBlocked);
        // The taskbar's overflow panel can open over a view; the pointer goes through to it. WinScreen's own menus
        // need no such thing: a click on the view while its menu is open just closes the menu.
        bool passToShell = _inputPaused && TopLayer.OverflowOpen;
        if (passToShell != _passToShell) { _passToShell = passToShell; ApplyClickThrough(); }
        FollowOriginalState();
        FollowOriginalMinimized();
        if (_suspended || IsDisposed) return;
        FollowCover();
        // A hidden original's link text, tooltips and anything it keeps open after a visit would show where it really is;
        // every fifth tick is enough.
        if (_live is null && _hideItem.Checked && _dropSince == 0 && _windows.IsHidden(_source) && ++_popupTick % 5 == 0)
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
            _live.Update(LiveMapping(PictureArea));
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
                _endReason = switchedAway ? $"switched to 0x{foreground:X} ({WindowInfo.ClassName(foreground)})"
                    : hookLost ? "the hook stopped seeing the pointer" : "the pointer was outside";
                EndLive();
            }
        }

        // While a drag is handed over the original is shown at another size, and the view is out of sight: it fits
        // again once back, so it comes back where it was.
        if (_pressed || _thumbnail is null || _pickingUp || _dropSince != 0 || SteppedAside)
            return;

        try
        {
            if (ShownPart(_thumbnail.SourceSize) != _lastShown)
                FitToSource();
        }
        catch (WindowOperationException caught)
        {
            // Apps can be briefly unmeasurable while they redraw; the next tick tries again.
            Diagnostics.Log.Swallowed(caught);
        }
    }

    private void FollowPointer() => UpdateHover(Cursor.Position, Environment.TickCount64);

    internal void UpdateHover(Point pointer, long now)
    {
        ClaimEscape(_selecting || _live?.Scrolling == true);
        if (_live?.Scrolling == true) Cursor.Current = _live.ScrollCursor;
        else if (_wasScrolling) Cursor.Current = Cursors.Default;
        _wasScrolling = _live?.Scrolling == true;
        // Stepped aside, the view is out of sight for its original: its parts stay away too, even with the pointer
        // where the view was.
        bool unavailable = _suspended || _selecting || _overrides.ClickThrough || (_inputPaused && !_menu.Visible) ||
            _steppedAside.Length > 0;
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

    internal bool TitleBarAlways => _overrides.TitleBarAlways ?? _viewPreferences.AlwaysShowTitleBar;

    private Rectangle PictureArea => PipGeometry.FitPicture(PipGeometry.ViewArea(ClientSize), _appliedTurn.Oriented(_lastShown));

    private PipMapping LiveMapping(Rectangle area) =>
        new(RectangleToScreen(area), ShownPart(_thumbnail!.SourceSize), _appliedTurn);

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
            if (!_fitting) _longSide = Math.Max(Width, Height);
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
             !PipGeometry.NeedsWholePreview(PictureArea.Width, _appliedTurn.Oriented(_lastShown).Width)) || !_origin.IsWindow || _lastShown.IsEmpty ||
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
            Point mapped = new PipMapping(PictureArea, shown, _appliedTurn).ToSource(PointToClient(pointer));
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

    // Without claiming more than WinScreen can tell.
    private void ShowState()
    {
        if (_offer != PressOffer.None && (Environment.TickCount64 >= _offerUntil || !CanOffer)) _offer = PressOffer.None;
        PipChipState state = PipStateChip.Decide(_origin.IsMinimized, BackendState,
            Bounds.Contains(Cursor.Position), _selecting || _pressed || _steppedAside.Length > 0, _offer);
        if (state == PipChipState.None)
        {
            _chip?.Hide();
            return;
        }

        if (_chip is null)
        {
            _chip = new PipStateChip(this);
            _chip.Chosen += OnChipChosen;
            KeepPartOutOfScreenCapture(_chip);
        }

        _chip.Show(state, new Point(Left + PipGeometry.Border, Bottom - PipGeometry.Border));
    }

    internal PipChipState ChipShown => _chip?.Saying ?? PipChipState.None;

    internal Rectangle? ChipBounds => _chip is { Visible: true } chip ? chip.Bounds : null;

    // Concealed first so it never flashes up, then brought back without taking the focus. Only a click on the chip does
    // this, never WinScreen by itself.
    internal void KeepUpdating()
    {
        SetOriginalHidden(true);
        if (_hideItem.Checked && User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_SHOWNOACTIVATE);
        StayOnTop();
    }

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

        ViewState.Modes before = CurrentModes();
        if (_hideItem.Checked) _interactiveBeforeShown = _interactiveItem.Checked;
        ChangeInteractionMode(false, false);
        CheckRequest(before, ViewState.Request.ShowOriginal);
        if (_windows.IsHidden(_source) || _windows.IsOutOfSwitcher(_source))
            return;
        if (User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_RESTORE);
        User32.SetForegroundWindow(_source);
        // On another desktop, Windows switches to the window's own and then may hand the focus to the last window
        // used there, often this view, which shows on every desktop. The view finishes the job once the switch is over.
        if (!VirtualDesktops.OnCurrentDesktop(_source))
        {
            _goingToUntil = Environment.TickCount64 + GoingToFor;
            _goingToSettledSince = 0;
        }
    }

    private long _goingToUntil;
    private const long GoingToFor = 5000;

    private void FinishGoingTo()
    {
        if (_goingToUntil == 0) return;
        // Windows cloaks windows for a moment while it switches desktops, so only time or a closed window ends this.
        if (Environment.TickCount64 > _goingToUntil || !_origin.IsWindow || !User32.IsWindow(_source))
        {
            _goingToUntil = 0;
            return;
        }
        if (_windows.IsHidden(_source)) return;
        if (!VirtualDesktops.OnCurrentDesktop(_source)) { _goingToSettledSince = 0; return; } // Still switching.
        // Only taken back from this view or from nothing, never from a window the person went to meanwhile.
        nint front = User32.GetForegroundWindow();
        if (front == Handle || front == 0)
        {
            GoingToTakes++;
            _goingToSettledSince = 0;
            User32.SetForegroundWindow(_source);
        }
        else if (front == _source)
        {
            long now = Environment.TickCount64;
            if (_goingToSettledSince == 0) _goingToSettledSince = now;
            else if (now - _goingToSettledSince >= GoingToSettled) _goingToUntil = 0;
        }
        else _goingToSettledSince = 0; // Another window, such as Windows' own during the switch: wait and see.
    }

    private long _goingToSettledSince;
    private const long GoingToSettled = 1500;

    internal int GoingToTakes { get; private set; }

    internal bool GoingTo => _goingToUntil != 0;
}
