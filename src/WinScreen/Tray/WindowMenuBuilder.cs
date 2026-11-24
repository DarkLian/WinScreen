using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Settings;
using WinScreen.Windows;
using static WinScreen.Localization.UiText;

namespace WinScreen.Tray;

// One command list and target check for the tray and pointer menus.
internal sealed class WindowMenuBuilder(Func<WindowIdentity?> target, Action<AppCommand, nint> run,
    Action<nint, Rectangle> fill, Func<AppCommand, bool> available, Func<AppCommand, nint, bool> enabled,
    Func<AppCommand, nint, bool> isOn, Action staleTarget)
{
    private sealed record Utility(bool NeedsPath);
    private sealed record DisplayMove(string Device);
    internal Action<nint, string>? MoveToDisplay { get; init; }
    private sealed record Resize(Size Size);

    internal Action<nint, Size>? ResizeTo { get; init; }

    internal static readonly Size[] PresetSizes = [new(800, 600), new(1024, 768), new(1280, 720), new(1366, 768), new(1600, 900), new(1920, 1080)];

    internal Func<AppCommand, string>? Hotkey { get; init; }

    internal Func<nint, int>? OpacityPercent { get; init; }
    internal Action<nint, int>? SetOpacityPercent { get; init; }

    // The sound of the video behind the window's Glass, or null when none plays there; EveryApp when it is Every
    // app's background, so changing it changes it for every app without its own.
    internal readonly record struct BackgroundSound(int Percent, bool EveryApp);
    internal Func<nint, BackgroundSound?>? BackgroundSoundOf { get; init; }
    internal Action<nint, int>? SetBackgroundSound { get; init; }
    internal Func<nint, bool>? HasBackground { get; init; }
    internal Action<nint>? EditBackground { get; init; }
    internal Func<nint, bool>? HasNextBackground { get; init; }
    internal Action<nint>? NextBackground { get; init; }
    internal static readonly int[] SoundChoices = [0, 25, 50, 75, 100];
    private sealed record SoundMenu;
    private sealed record SoundChoice(int Percent);
    private sealed record BackgroundEdit;
    private sealed record BackgroundNext;

    private sealed record Group;
    private sealed record MoreGroup;

    public void Populate(ToolStripItemCollection items, bool tray)
    {
        items.Add(Command(AppCommand.TogglePin));
        AddOpacityRow(items);
        items.Add(Command(AppCommand.ToggleClickThrough));

        items.Add(MenuParts.Header(T("menu.group.elsewhere")));
        items.Add(Command(AppCommand.OpenPip));
        items.Add(Command(AppCommand.ToggleAnchor));
        items.Add(Command(AppCommand.MinimizeToAnchor));

        items.Add(MenuParts.Header(T("menu.group.place")));
        items.Add(new PlacementRow(place => WithTarget(window => run(place, window)), command => Hotkey?.Invoke(command)) { Tag = AppCommand.AlignCenter });
        ToolStripMenuItem arrange = Command(AppCommand.Arrange);
        PopulateArrange(arrange.DropDownItems, places: false); // The grid above already offers the nine places.
        items.Add(arrange);

        items.Add(MenuParts.Header(T("menu.group.look")));
        items.Add(Command(AppCommand.ToggleDarkTitleBar));
        var borderless = new ToolStripMenuItem(T("window.borderless")).WithIcon(ColorIcons.Borderless);
        borderless.DropDownItems.Add(Command(AppCommand.ToggleBorderless));
        var fillItem = new ToolStripMenuItem(T("window.fillDisplay"), MenuParts.Icon(MenuParts.FullScreen), (_, _) =>
        {
            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 1) WithTarget(window => fill(window, screens[0].Bounds));
        });
        void RefreshDisplays()
        {
            while (fillItem.DropDownItems.Count > 0) fillItem.DropDownItems[0].Dispose();
            Screen[] screens = Screen.AllScreens;
            if (screens.Length > 1)
                foreach (Screen screen in screens)
                    fillItem.DropDownItems.Add(screen.DeviceName, null, (_, _) => WithTarget(window =>
                    {
                        Screen? current = Screen.AllScreens.FirstOrDefault(item => item.DeviceName == screen.DeviceName);
                        if (current is null || current.Bounds != screen.Bounds) { staleTarget(); return; }
                        fill(window, current.Bounds);
                    }));
        }
        RefreshDisplays();
        borderless.DropDownOpening += (_, _) => RefreshDisplays();
        fillItem.Tag = AppCommand.ToggleBorderlessFill;
        borderless.DropDownItems.Add(fillItem);
        items.Add(borderless);
        var effects = new ToolStripMenuItem(T("menu.effects")) { Tag = new Group() }.WithIcon(MenuParts.Repair);
        foreach (AppCommand command in new[] { AppCommand.ToggleDark, AppCommand.ToggleGlass, AppCommand.GlassLevels, AppCommand.KeepAreas, AppCommand.ToggleShrink, AppCommand.ToggleCrop })
            effects.DropDownItems.Add(CommandCatalog.IsExperimental(command) ? Command(command).Experimental() : Command(command));
        if (BackgroundSoundOf is not null && SetBackgroundSound is { } setSound)
        {
            var sound = new ToolStripMenuItem(T("menu.background.sound")) { Tag = new SoundMenu() }.WithIcon(MenuParts.Volume);
            foreach (int percent in SoundChoices)
                sound.DropDownItems.Add(StayingOpen.Keep(new ToolStripMenuItem(percent == 0 ? T("menu.background.soundOff") : $"{percent}%", null,
                    (_, _) => WithTarget(window => setSound(window, percent))) { Tag = new SoundChoice(percent) }.OneOf()));
            effects.DropDownItems.Add(sound);
        }
        if (NextBackground is { } nextBackground)
            effects.DropDownItems.Add(StayingOpen.Keep(new ToolStripMenuItem(T("menu.background.next"), MenuParts.Icon(MenuParts.Rotate),
                (_, _) => WithTarget(nextBackground)) { Tag = new BackgroundNext() }));
        if (EditBackground is { } editBackground)
            effects.DropDownItems.Add(new ToolStripMenuItem(T("menu.background.edit"), MenuParts.Icon(MenuParts.Picture),
                (_, _) => WithTarget(editBackground)) { Tag = new BackgroundEdit() });
        items.Add(effects);

        items.Add(new ToolStripSeparator());
        var more = new ToolStripMenuItem(T("menu.more")) { Tag = new MoreGroup() }.WithIcon(MenuParts.More);
        if (OpacityPercent is null || SetOpacityPercent is null)
        {
            more.DropDownItems.Add(Command(AppCommand.OpacityDown));
            more.DropDownItems.Add(Command(AppCommand.OpacityUp));
        }
        more.DropDownItems.Add(Command(AppCommand.ToggleDefaultOpacity));
        more.DropDownItems.Add(Command(AppCommand.SaveWindowSetup));
        more.DropDownItems.Add(new ToolStripSeparator());
        var copy = new ToolStripMenuItem(T("common.copy")).WithIcon(MenuParts.Copy);
        foreach (var (label, index) in new[] { (T("window.info.title"), 0), (T("window.info.hwnd"), 3), (T("window.info.processId"), 4), (T("window.info.path"), 2) })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => WithTarget(window =>
                WindowUtilities.CopyText(WindowInfo.Facts(window)[index].Value))) { Tag = new Utility(index == 2) };
            copy.DropDownItems.Add(item);
        }
        copy.DropDownItems.Add(T("window.copy.all"), null, (_, _) => WithTarget(window =>
            WindowUtilities.CopyText(WindowUtilities.Information(window)))).Tag = new Utility(false);
        more.DropDownItems.Add(copy);
        more.DropDownItems.Add(new ToolStripMenuItem(T("window.openLocation"), MenuParts.Icon(MenuParts.Folder),
            (_, _) => WithTarget(WindowUtilities.OpenLocation)) { Tag = new Utility(true) });
        items.Add(more);
        items.Add(Command(AppCommand.RestoreWindow));
    }

    private void AddOpacityRow(ToolStripItemCollection items)
    {
        if (OpacityPercent is not null && SetOpacityPercent is { } setOpacity)
            items.Add(new OpacityRow(T("pip.opacity"), percent => WithTarget(window => setOpacity(window, percent))));
    }

    internal void PopulateOpacity(ToolStripItemCollection items)
    {
        AddOpacityRow(items);
        items.Add(Command(AppCommand.ToggleDefaultOpacity));
    }

    internal void PopulateArrange(ToolStripItemCollection items, bool places = true)
    {
        IEnumerable<AppCommand?> actions = places ? CommandCatalog.ArrangeMenu
            : CommandCatalog.ArrangeMenu.SkipWhile(action => action is { } entry && CommandCatalog.AnchorOf(entry) is not null).Skip(1);
        foreach (AppCommand? action in actions)
            items.Add(action is not { } entry ? new ToolStripSeparator()
                : CommandCatalog.IsExperimental(entry) ? Command(entry).Experimental() : Command(entry));
        if (MoveToDisplay is { } move)
        {
            var displays = new ToolStripMenuItem(T("placement.moveToDisplay")).WithIcon(ColorIcons.Displays);
            foreach (Screen screen in Screen.AllScreens)
            {
                string device = screen.DeviceName;
                displays.DropDownItems.Add($"{device} ({screen.Bounds.Width}×{screen.Bounds.Height})", null,
                    (_, _) => WithTarget(window => move(window, device))).Tag = new DisplayMove(device);
            }
            items.Insert(places ? 10 : 0, displays);
        }
        if (ResizeTo is { } resize)
        {
            var sizes = new ToolStripMenuItem(T("placement.size")).WithIcon(ColorIcons.WindowSize);
            foreach (Size size in PresetSizes)
                sizes.DropDownItems.Add($"{size.Width} × {size.Height}", null,
                    (_, _) => WithTarget(window => resize(window, size))).Tag = new Resize(size);
            items.Insert(Math.Min(items.Count, (places ? 10 : 0) + (MoveToDisplay is null ? 0 : 1)), sizes);
        }
    }

    internal static void LeaveOut(ToolStripItemCollection items, IReadOnlySet<AppCommand> onBar)
    {
        bool Shown(ToolStripItem item) => item switch
        {
            PlacementRow => onBar.Contains(AppCommand.Arrange),
            OpacityRow => onBar.Contains(AppCommand.ToggleDefaultOpacity),
            // With several displays the menu asks which one; the button fills the one the window is on.
            ToolStripMenuItem { Tag: AppCommand.ToggleBorderlessFill, HasDropDownItems: true } => false,
            { Tag: AppCommand command } => onBar.Contains(command),
            _ => false,
        };
        foreach (ToolStripItem item in items.Cast<ToolStripItem>().ToArray())
        {
            if (Shown(item)) { items.Remove(item); item.Dispose(); continue; }
            if (item is not ToolStripMenuItem { HasDropDownItems: true } parent || item.Tag is AppCommand) continue;
            LeaveOut(parent.DropDownItems, onBar);
            if (!parent.DropDownItems.OfType<ToolStripMenuItem>().Any()) { items.Remove(item); item.Dispose(); continue; }
            if (item.Tag is not MoreGroup) continue;
            int at = items.IndexOf(item);
            items.Insert(at++, new ToolStripSeparator());
            foreach (ToolStripItem child in parent.DropDownItems.Cast<ToolStripItem>().ToArray()) items.Insert(at++, child);
            items.Remove(item);
            item.Dispose();
        }
        Tidy(items);
    }

    private static void Tidy(ToolStripItemCollection items)
    {
        bool Ends(ToolStripItem? item) => item is null or ToolStripSeparator || Equals(item.Tag, MenuParts.HeaderTag);
        for (int index = items.Count - 1; index >= 0; index--)
        {
            ToolStripItem item = items[index];
            ToolStripItem? next = index + 1 < items.Count ? items[index + 1] : null;
            bool heading = Equals(item.Tag, MenuParts.HeaderTag);
            if (heading && Ends(next) || item is ToolStripSeparator && (index == 0 || next is null or ToolStripSeparator))
            {
                items.RemoveAt(index);
                item.Dispose();
            }
        }
        while (items.Count > 0 && items[0] is ToolStripSeparator first) { items.RemoveAt(0); first.Dispose(); }
    }

    private ToolStripMenuItem Command(AppCommand command) => new(CommandCatalog.Name(command),
        MenuParts.IconOf(command) is { } glyph ? MenuParts.Icon(glyph) : null, (_, _) =>
    {
        if (command == AppCommand.RestoreAll) run(command, 0);
        else WithTarget(window => run(command, window));
    }) { Tag = command, ShortcutKeyDisplayString = Hotkey?.Invoke(command) is { Length: > 0 } keys ? keys : null };

    private void WithTarget(Action<nint> action)
    {
        if (target() is not { } identity || !(identity.Token == 0 ? identity.IsSameWindow() : identity.Matches())) { staleTarget(); return; }
        action(identity.Window);
    }

    public void Refresh(ToolStripItemCollection items)
    {
        nint window = target()?.Window ?? 0;
        foreach (ToolStripItem entry in items)
        {
            if (entry is OpacityRow opacity)
            {
                opacity.Enabled = window != 0 && enabled(AppCommand.OpacityUp, window);
                if (window != 0 && OpacityPercent is { } read) opacity.Value = read(window);
            }
            else if (entry is PlacementRow places)
                places.Enabled = window != 0 && enabled(AppCommand.AlignCenter, window);
            if (entry is not ToolStripMenuItem item) continue;
            if (item.Tag is AppCommand command)
            {
                item.Available = available(command);
                item.Checked = window != 0 && isOn(command, window);
                item.Enabled = command == AppCommand.RestoreAll || window != 0 && enabled(command, window);
                MenuParts.ShowKeys(item, Hotkey?.Invoke(command));
            }
            else if (item.Tag is SoundMenu)
            {
                BackgroundSound? sound = window == 0 ? null : BackgroundSoundOf?.Invoke(window);
                item.Available = sound is not null;
                if (sound is { } now)
                {
                    item.Text = now.EveryApp ? T("menu.background.soundEveryApp") : T("menu.background.sound");
                    int nearest = SoundChoices.MinBy(percent => Math.Abs(percent - now.Percent));
                    foreach (ToolStripMenuItem choice in item.DropDownItems.OfType<ToolStripMenuItem>())
                        choice.Checked = choice.Tag is SoundChoice { Percent: var percent } && percent == nearest;
                }
                continue;
            }
            else if (item.Tag is BackgroundEdit)
                item.Available = window != 0 && HasBackground?.Invoke(window) == true;
            else if (item.Tag is BackgroundNext)
                item.Available = window != 0 && HasNextBackground?.Invoke(window) == true;
            else if (item.Tag is Utility utility)
                item.Enabled = window != 0 && (!utility.NeedsPath || WindowInfo.ExecutablePath(window).Length > 0);
            else if (item.Tag is Resize)
                item.Enabled = window != 0 && enabled(AppCommand.PlaceExactly, window);
            else if (item.Tag is DisplayMove display)
                item.Enabled = window != 0 && enabled(AppCommand.MoveToNextDisplay, window) &&
                    Screen.AllScreens.Any(screen => screen.DeviceName == display.Device);
            if (item.HasDropDownItems) Refresh(item.DropDownItems);
            // Available is what was asked for; Visible would also say whether the menu is open.
            if (item.Tag is Group) item.Available = item.DropDownItems.OfType<ToolStripMenuItem>().Any(child => child.Available);
        }
    }
}

internal static class WindowUtilities
{
    public static string Information(nint window) => string.Join(Environment.NewLine,
        WindowInfo.Facts(window).Select(fact => $"{fact.Label}: {fact.Value}"));

    public static void CopyText(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (ExternalException caught) { Diagnostics.Log.Swallowed(caught); }
    }

    public static void OpenLocation(nint window)
    {
        string path = WindowInfo.ExecutablePath(window);
        if (path.Length == 0) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false };
            Process.Start(start)?.Dispose();
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException) { Diagnostics.Log.Swallowed(failure); }
    }
}
