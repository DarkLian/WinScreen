using static WinScreen.Localization.UiText;
using System.Diagnostics.CodeAnalysis;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>The view's own menu: how it is built, what it offers as the view changes, and its submenus.</summary>
public sealed partial class PipWindow
{
    /// <summary>Builds the view's menu, once, from the constructor.</summary>
    [MemberNotNull(nameof(_hideItem), nameof(_goToItem), nameof(_muteItem), nameof(_videoItem), nameof(_useOriginalItem), nameof(_autoMoveItem), nameof(_removeCropItem), nameof(_viewOnlyItem),
        nameof(_helpItem), nameof(_useHeader), nameof(_originalHeader), nameof(_interactiveItem), nameof(_middleScrollItem),
        nameof(_enhancedHoverItem))]
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
        _muteItem = new ToolStripMenuItem(T("menu.original.mute"), null, (_, _) => ChooseMute(!_muteItem!.Checked));
        _menu.Items.Add(_muteItem);

        _menu.Items.Add(MenuParts.Header(T("menu.group.picture")));
        _removeCropItem = new ToolStripMenuItem(T("pip.zoom.reset"), MenuParts.Icon(MenuParts.Crop),
            (_, _) => { ResetZoom(); SetCrop(Rectangle.Empty); });
        _menu.Items.Add(_removeCropItem);
        _videoItem = new ToolStripMenuItem(T("pip.video.follow"), null, (_, _) => FollowLargestVideo(!_followingVideo));
        _menu.Items.Add(_videoItem);
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
        foreach (ToolStripItem item in new ToolStripItem[] { _hideItem, _muteItem, _autoMoveItem, _interactiveItem, _viewOnlyItem, _middleScrollItem, _enhancedHoverItem })
            StayingOpen.Keep(item);
        StayingOpen.Follow(_menu, RefreshMenuItems);
        _menu.Closed += (_, _) => { ReturnFocusAfterMenu(); PauseInput(TopLayer.InputBlocked); };
        ContextMenuStrip = _menu;
    }

    /// <summary>What the menu offers and ticks, for the view as it is now: when it opens, and after a choice that keeps it open.</summary>
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
        _useOriginalItem.Available = _origin.IsWindow && _hideItem.Checked;
        bool? muted = _origin.IsWindow ? SoundState?.Invoke() : null;
        _muteItem.Available = muted is not null;
        _muteItem.Checked = muted == true;
        if (muted is not null)
            _muteItem.Text = PipEnhancedHover.Describe(_source).App is PipHoverApp.Browser or PipHoverApp.Firefox
                ? T("menu.original.muteBrowser") : T("menu.original.mute");
        // The view closes soon after its window does; until then nothing offers to act on a window that is gone.
        bool there = _origin.IsAvailable;
        _goToItem.Enabled = _useOriginalItem.Enabled = _interactiveItem.Enabled = there;
        if (!there) _hideItem.Enabled = false;
        _removeCropItem.Enabled = !_crop.IsEmpty || _zoom != PipGeometry.WholeView;
        _videoItem.Checked = _followingVideo;
    }

    /// <summary>Mutes or unmutes the original's program, and says so when that couldn't be done.</summary>
    private void ChooseMute(bool on)
    {
        if (SetSound?.Invoke(on) == false) Problem?.Invoke(T("errors.sound.failed"));
        _muteItem.Checked = SoundState?.Invoke() == true;
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

    private void ReturnFocusAfterMenu()
    {
        if (!IsHandleCreated || IsDisposed || User32.GetForegroundWindow() != Handle)
            return;
        bool hiddenSource = _foregroundBeforeMenu == _source && _hideItem.Checked;
        if (_foregroundBeforeMenu != 0 && !hiddenSource)
            User32.SetForegroundWindow(_foregroundBeforeMenu);
    }
}
