using static WinScreen.Localization.UiText;
using System.Diagnostics.CodeAnalysis;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;

namespace WinScreen.Pip;

public sealed partial class PipWindow
{
    [MemberNotNull(nameof(_hideItem), nameof(_awayItem), nameof(_goToItem), nameof(_muteItem), nameof(_useOriginalItem), nameof(_autoMoveItem), nameof(_removeCropItem), nameof(_viewOnlyItem),
        nameof(_helpItem), nameof(_useHeader), nameof(_originalHeader), nameof(_interactiveItem), nameof(_middleScrollItem),
        nameof(_enhancedHoverItem), nameof(_freezeItem), nameof(_turnItem), nameof(_uprightItem))]
    private void BuildMenu()
    {
        _hideItem = new ToolStripMenuItem(T("menu.original.keepHidden"), null,
            (_, _) => SetOriginalHidden(!_hideItem!.Checked));
        _autoMoveItem = new ToolStripMenuItem(T("pip.autoMove"), null,
            (_, _) => SetAutoMove(!_autoMoveItem!.Checked)).Experimental();
        _interactiveItem = new ToolStripMenuItem(T("menu.view.interactive"), null,
            (_, _) => SetInteractive(!_interactiveItem!.Checked));
        // Tagged Experimental unless this kind of app was verified for it. Its two options are experimental for every
        // app; under an untagged Interactive they carry the tag themselves.
        if (!_interactiveWithoutSwitch) _interactiveItem.Experimental();
        _interactiveItem.OneOf();
        _middleScrollItem = new ToolStripMenuItem(T("menu.view.middleScroll"), null,
            (_, _) => SetMiddleClickScroll(!_middleScrollItem!.Checked)) { ToolTipText = T("menu.view.middleScroll.tip") };
        _enhancedHoverItem = new ToolStripMenuItem(T("menu.view.hover"), null,
            (_, _) => { _hoverChosen = true; SetEnhancedHover(!_enhancedHoverItem!.Checked); })
            { ToolTipText = T("pip.enhancedHover.description"), Checked = PipEnhancedHover.StartsOn(_source) };
        if (_interactiveWithoutSwitch) { _middleScrollItem.Experimental(); _enhancedHoverItem.Experimental(); }
        _viewOnlyItem = new ToolStripMenuItem(T("menu.view.watch"), null, (_, _) => SetInteractive(false)) { Name = "viewOnly" }.OneOf();
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
        _awayItem = new ToolStripMenuItem(T("menu.original.awayHidden"), MenuParts.Icon(MenuParts.View)) { Enabled = false };
        _menu.Items.Add(_awayItem);
        _hideItem.Image = MenuParts.Icon(MenuParts.View);
        _menu.Items.Add(_hideItem);
        _useOriginalItem = _menu.Items.Add(T("menu.original.use"), MenuParts.Icon(MenuParts.Undo), (_, _) => UseOriginal());
        _goToItem = _menu.Items.Add(T("menu.original.goTo"), MenuParts.Icon(MenuParts.NewWindow), (_, _) => GoToSource());
        _muteItem = new ToolStripMenuItem(T("menu.original.mute"), MenuParts.Icon(MenuParts.Volume), (_, _) => ChooseMute(!_muteItem!.Checked));
        _menu.Items.Add(_muteItem);

        _menu.Items.Add(MenuParts.Header(T("menu.group.picture")));
        _removeCropItem = new ToolStripMenuItem(T("pip.zoom.reset"), MenuParts.Icon(MenuParts.Crop),
            (_, _) => { ResetZoom(); SetCrop(Rectangle.Empty); });
        _menu.Items.Add(_removeCropItem);
        _freezeItem = new ToolStripMenuItem(T("pip.freeze"), MenuParts.Icon(MenuParts.Pause), (_, _) => SetFrozen(!Frozen));
        _menu.Items.Add(_freezeItem);
        _turnItem = new ToolStripMenuItem(T("pip.turn")).WithIcon(MenuParts.Rotate);
        foreach ((string text, AppCommand command, Func<ViewTurn, ViewTurn> next) in new (string, AppCommand, Func<ViewTurn, ViewTurn>)[]
        {
            (T("pip.turn.right"), AppCommand.ViewRotateRight, turn => turn.RotatedRight),
            (T("pip.turn.left"), AppCommand.ViewRotateLeft, turn => turn.RotatedLeft),
            (T("pip.turn.across"), AppCommand.ViewMirrorAcross, turn => turn.FlippedAcross),
            (T("pip.turn.down"), AppCommand.ViewMirrorDown, turn => turn.FlippedDown),
        })
            _turnItem.DropDownItems.Add(_keyed[command] = StayingOpen.Keep(new ToolStripMenuItem(text, null, (_, _) => SetTurn(next(_turn)))));
        _uprightItem = StayingOpen.Keep(new ToolStripMenuItem(T("pip.turn.upright"), null, (_, _) => SetTurn(ViewTurn.Upright)));
        _turnItem.DropDownItems.Add(_uprightItem);
        _turnItem.DropDownOpening += (_, _) => RefreshTurnItems();
        _menu.Items.Add(_turnItem);
        _copyPictureItem = new ToolStripMenuItem(T("pip.picture.copy"), MenuParts.Icon(MenuParts.Copy), (_, _) => CopyPicture());
        _freezeCopyItem = new ToolStripMenuItem(T("pip.picture.freezeCopy"), MenuParts.Icon(MenuParts.Picture), (_, _) => FreezeCopy());
        _copyTextItem = new ToolStripMenuItem(T("pip.picture.copyText"), MenuParts.Icon(MenuParts.Text), (_, _) => CopyText());
        _coverItem = new ToolStripMenuItem(T("pip.picture.cover"), MenuParts.Icon(MenuParts.Hide), (_, _) => EditCovers());
        _uncoverItem = new ToolStripMenuItem(T("pip.picture.uncover"), MenuParts.Icon(MenuParts.View), (_, _) => SetCovers([]));
        _recordItem = new ToolStripMenuItem(T("pip.picture.record"), MenuParts.Icon(MenuParts.Record), (_, _) => ToggleRecording());
        _recordNote = new NoteRow(T("pip.record.noCapture"));
        _menu.Items.AddRange([_copyPictureItem, _freezeCopyItem, _copyTextItem, _recordItem, _recordNote, _coverItem, _uncoverItem]);
        _menu.Items.Add(options);
        _menu.Items.Add(presets);

        _menu.Items.Add(new ToolStripSeparator());
        _helpItem = new ToolStripMenuItem(T("menu.help")).WithIcon(MenuParts.Help);
        _retryItem = new ToolStripMenuItem(T("pip.backend.retry"), MenuParts.Icon(MenuParts.Sync), (_, _) => RetryCapture());
        _helpItem.DropDownItems.Add(new ToolStripSeparator());
        _helpItem.DropDownItems.Add(T("pip.diagnostics.title"), MenuParts.Icon(MenuParts.Diagnostic), (_, _) => ShowDiagnostics());
        _helpItem.DropDownItems.Add(_retryItem);
        _menu.Items.Add(_helpItem);
        var minimize = (ToolStripMenuItem)_menu.Items.Add(T("pip.minimize"), MenuParts.Icon(MenuParts.Minimize), (_, _) => SuspendView());
        _menu.Items.Add(T("pip.close"), MenuParts.Icon(MenuParts.Close), (_, _) => Close());
        _keyed[AppCommand.ViewKeepHidden] = _hideItem;
        _keyed[AppCommand.ViewInteractive] = _interactiveItem;
        _keyed[AppCommand.ViewUseOriginal] = (ToolStripMenuItem)_useOriginalItem;
        _keyed[AppCommand.ViewGoToOriginal] = (ToolStripMenuItem)_goToItem;
        _keyed[AppCommand.ViewMute] = _muteItem;
        _keyed[AppCommand.ViewFreeze] = _freezeItem;
        _keyed[AppCommand.ViewUpright] = _uprightItem;
        _keyed[AppCommand.ViewResetZoom] = _removeCropItem;
        _keyed[AppCommand.ViewMinimize] = minimize;
        _keyed[AppCommand.ViewCopyPicture] = _copyPictureItem;
        _keyed[AppCommand.ViewFreezeCopy] = _freezeCopyItem;
        _keyed[AppCommand.ViewCopyText] = _copyTextItem;
        _keyed[AppCommand.ViewCover] = _coverItem;
        _keyed[AppCommand.ViewRecord] = _recordItem;
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
        foreach (ToolStripItem item in new ToolStripItem[] { _hideItem, _muteItem, _autoMoveItem, _interactiveItem, _viewOnlyItem, _middleScrollItem, _enhancedHoverItem, _freezeItem })
            StayingOpen.Keep(item);
        StayingOpen.Follow(_menu, RefreshMenuItems);
        _menu.Closed += (_, _) => { ReturnFocusAfterMenu(); PauseInput(TopLayer.InputBlocked); };
        ContextMenuStrip = _menu;
    }

    private void RefreshMenuItems()
    {
        _menu.Items["inputStatus"]!.Text = MenuTitle;
        FillHelp();
        _retryItem.Available = CanRetryCapture;
        _autoMoveItem.Available = AllowAutoMove;
        bool canInteract = CanInteract;
        _viewOnlyItem.Available = _interactiveItem.Available = canInteract;
        _viewOnlyItem.Checked = !_interactiveItem.Checked;
        // Available is what is asked for; Visible would also say whether the menu is open, and it isn't yet. The two
        // options are experimental, also for a kind of app that can be used directly without the switch.
        bool showInteractionOptions = canInteract && _interactiveItem.Checked && _allowInteractive;
        _middleScrollItem.Available = showInteractionOptions;
        _enhancedHoverItem.Available = showInteractionOptions;
        _useHeader.Available = canInteract || AllowAutoMove;
        _enhancedHoverItem.Enabled = _enhancedHoverItem.Checked || PipEnhancedHover.CanAttempt(_source);
        // With Enhanced hover on, a browser's own middle-click scrolling works and is the better one.
        _middleScrollItem.Enabled = !(showInteractionOptions && _enhancedHoverItem.Checked && AppScrollsWithHover);
        _originalHeader.Available = _hideItem.Available = _goToItem.Available = _origin.IsWindow;
        _awayItem.Available = _origin.IsWindow && _awayFrom != Guid.Empty;
        _useOriginalItem.Available = _origin.IsWindow && _hideItem.Checked;
        bool? muted = _origin.IsWindow ? SoundState?.Invoke() : null;
        _muteItem.Available = muted is not null;
        _muteItem.Checked = muted == true;
        if (_recording is { } recording && muted is not null) recording.Muted = muted == true;
        if (muted is not null)
            _muteItem.Text = PipEnhancedHover.Describe(_source).App is PipHoverApp.Browser or PipHoverApp.Firefox
                ? T("menu.original.muteBrowser") : T("menu.original.mute");
        // The view closes soon after its window does; until then nothing offers to act on a window that is gone.
        bool there = _origin.IsAvailable;
        _goToItem.Enabled = _useOriginalItem.Enabled = _interactiveItem.Enabled = there;
        if (!there) _hideItem.Enabled = false;
        _removeCropItem.Enabled = !_crop.IsEmpty || _zoom != PipGeometry.WholeView;
        // The compositor's picture can't be held, only a captured one.
        _freezeItem.Checked = Frozen;
        _freezeItem.Enabled = Frozen || _thumbnail?.CanFreeze == true;
        bool picture = _thumbnail is not null;
        _copyPictureItem.Enabled = _freezeCopyItem.Enabled = _copyTextItem.Enabled = _coverItem.Enabled = picture;
        _uncoverItem.Available = _covers.Count > 0;
        // Greyed with the reason beneath it, never in a tooltip.
        string? refused = _recording is null && picture ? RecordingRefusedBecause() : null;
        _recordItem.Text = _recording is null ? T("pip.picture.record") : T("pip.picture.stopRecording");
        _recordItem.Enabled = picture && refused is null;
        _recordNote.Text = refused ?? "";
        _recordNote.Available = refused is not null;
        // Only a captured picture can be turned.
        _turnItem.Enabled = CanTurn || !_turn.IsUpright;
        RefreshTurnItems();
        foreach ((AppCommand command, ToolStripMenuItem item) in _keyed) MenuParts.ShowKeys(item, Hotkey?.Invoke(command));
    }

    private readonly Dictionary<AppCommand, ToolStripMenuItem> _keyed = [];
    private ToolStripMenuItem _copyPictureItem = null!, _freezeCopyItem = null!, _copyTextItem = null!, _coverItem = null!, _uncoverItem = null!,
        _recordItem = null!;
    private NoteRow _recordNote = null!;

    // The keys set for the view commands, shown beside the items they do.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<AppCommand, string?>? Hotkey { get; set; }

    // A view command from a key does what its menu item does, and only when the menu would offer it.
    internal void Run(AppCommand command)
    {
        bool there = _origin.IsWindow && _origin.IsAvailable;
        // Minimized, the view has no picture to hide its original behind: the key brings it back first.
        if (command is AppCommand.ViewKeepHidden or AppCommand.ViewInteractive && _suspended && there)
        {
            ResumeView();
            if (_suspended) return;
        }
        // A minimized view shows no picture to copy, read or cover: the key brings it back first, as its menu would.
        if (command is AppCommand.ViewCopyPicture or AppCommand.ViewFreezeCopy or AppCommand.ViewCopyText or AppCommand.ViewCover or AppCommand.ViewRecord && _suspended)
        {
            ResumeView();
            if (_suspended) return;
        }
        switch (command)
        {
            case AppCommand.ViewCopyPicture when _thumbnail is not null: CopyPicture(); break;
            case AppCommand.ViewFreezeCopy when _thumbnail is not null: FreezeCopy(); break;
            case AppCommand.ViewCopyText when _thumbnail is not null: CopyText(); break;
            case AppCommand.ViewCover when _thumbnail is not null: EditCovers(); break;
            case AppCommand.ViewRecord when _thumbnail is not null: ToggleRecording(); break;
            case AppCommand.ViewKeepHidden when there && !_interactiveItem.Checked: SetOriginalHidden(!_hideItem.Checked); break;
            case AppCommand.ViewInteractive when there && CanInteract: SetInteractive(!_interactiveItem.Checked); break;
            case AppCommand.ViewUseOriginal when there && _hideItem.Checked: UseOriginal(); break;
            case AppCommand.ViewGoToOriginal when there: GoToSource(); break;
            case AppCommand.ViewMute when _origin.IsWindow && SoundState?.Invoke() is bool muted: ChooseMute(!muted); break;
            case AppCommand.ViewFreeze when Frozen || _thumbnail?.CanFreeze == true: SetFrozen(!Frozen); break;
            case AppCommand.ViewRotateRight when CanTurn: SetTurn(_turn.RotatedRight); break;
            case AppCommand.ViewRotateLeft when CanTurn: SetTurn(_turn.RotatedLeft); break;
            case AppCommand.ViewMirrorAcross when CanTurn: SetTurn(_turn.FlippedAcross); break;
            case AppCommand.ViewMirrorDown when CanTurn: SetTurn(_turn.FlippedDown); break;
            case AppCommand.ViewUpright when !_turn.IsUpright: SetTurn(ViewTurn.Upright); break;
            case AppCommand.ViewResetZoom when !_crop.IsEmpty || _zoom != PipGeometry.WholeView: ResetZoom(); SetCrop(Rectangle.Empty); break;
            case AppCommand.ViewMinimize when _suspended: ResumeView(); break;
            case AppCommand.ViewMinimize: SuspendView(); break;
        }
    }

    internal bool IsOn(AppCommand command) => command switch
    {
        AppCommand.ViewKeepHidden => _hideItem.Checked,
        AppCommand.ViewInteractive => _interactiveItem.Checked,
        AppCommand.ViewMute => _origin.IsWindow && SoundState?.Invoke() == true,
        AppCommand.ViewFreeze => Frozen,
        AppCommand.ViewMinimize => _suspended,
        AppCommand.ViewCover => _covers.Count > 0,
        AppCommand.ViewRecord => _recording is not null,
        _ => false,
    };

    private void RefreshTurnItems()
    {
        foreach (ToolStripItem item in _turnItem.DropDownItems)
            item.Enabled = item == _uprightItem ? !_turn.IsUpright : CanTurn;
    }

    private void ChooseMute(bool on)
    {
        if (SetSound?.Invoke(on) == false) Problem?.Invoke(T("errors.sound.failed"));
        _muteItem.Checked = SoundState?.Invoke() == true;
        if (_recording is { } recording) recording.Muted = _muteItem.Checked;
    }

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
        // A display or region: what it shows, and the view as it is, to open again from Saved sources.
        if (_origin is ScreenPipSource)
            presets.DropDownItems.Add(T("pip.bookmarks.save"), null, (_, _) => SaveSourceBookmark());
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
        var layout = new CheckBox { Text = T("pip.bookmarks.includeLayout"), AutoSize = true, Checked = true };
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
            if (SourceBookmark(name.Text.Trim(), layout.Checked) is { } bookmark) SourceBookmarkSaved?.Invoke(bookmark);
        });
    }

    // With the view, everything about it comes back: crop, options, turn, and its exact place and size in pixels.
    internal PipSourceBookmark? SourceBookmark(string name, bool withView)
    {
        if (_origin is not ScreenPipSource source) return null;
        source.Require();
        Rectangle display = source.Screen.Bounds;
        PipPreset? view = null;
        if (withView)
        {
            view = CurrentPreset(name);
            view.ViewX = Bounds.X;
            view.ViewY = Bounds.Y;
            view.ViewWidth = Bounds.Width;
            view.ViewHeight = Bounds.Height;
        }
        return new PipSourceBookmark
        {
            Name = name, Device = source.Device,
            DisplayX = display.X, DisplayY = display.Y, DisplayWidth = display.Width, DisplayHeight = display.Height,
            Region = source.Region is { } region ? PipCropShares.Of(region, display.Size) : null,
            View = view,
        };
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
        if (preset.KeepAspectRatio is { } keep && keep != _overrides.KeepAspectRatio) changes.Add(T("pip.keepAspectRatio") + ": " + (keep ? T("common.yes") : T("common.no")));
        if (preset.Turn != _turn) changes.Add(T("pip.turn"));
        if (preset.Covers is { } covers && !covers.SequenceEqual(current.Covers ?? [])) changes.Add(T("pip.covers.preset"));
        return changes.Count == 0 ? T("pip.presets.noChanges") : string.Join("; ", changes);
    }

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

    private static ToolStripMenuItem Toggle(string text, bool on, Action flip) =>
        StayingOpen.Keep(new ToolStripMenuItem(text, null, (sender, _) =>
        {
            flip();
            ((ToolStripMenuItem)sender!).Checked ^= true;
        }) { Checked = on });

    private void ReturnFocusAfterMenu()
    {
        if (!IsHandleCreated || IsDisposed || User32.GetForegroundWindow() != Handle)
            return;
        bool hiddenSource = _foregroundBeforeMenu == _source && _hideItem.Checked;
        if (_foregroundBeforeMenu != 0 && !hiddenSource)
            User32.SetForegroundWindow(_foregroundBeforeMenu);
    }
}
