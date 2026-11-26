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


    private sealed record Group;
    private sealed record MoreGroup;

    public void Populate(ToolStripItemCollection items, bool tray)
    {
        // The same groups and order as the toolbar's buttons (CommandCatalog.ToolbarButtons).
        items.Add(Command(AppCommand.TogglePin));
        PopulateOpacity(items);
        items.Add(Command(AppCommand.ToggleClickThrough));

        items.Add(MenuParts.Header(T("menu.group.elsewhere")));
        items.Add(Command(AppCommand.OpenPip));
        items.Add(Command(AppCommand.ToggleAnchor));
        items.Add(Command(AppCommand.MinimizeToAnchor));

        items.Add(MenuParts.Header(T("menu.group.look")));
        items.Add(Entry(AppCommand.ToggleGlass));
        items.Add(Entry(AppCommand.ToggleDark));
        items.Add(Command(AppCommand.ToggleBorderless));
        items.Add(new FillDisplay(WithTarget, fill, staleTarget, Hotkey?.Invoke(AppCommand.ToggleBorderlessFill)));
        var effects = new ToolStripMenuItem(T("menu.effects")) { Tag = new Group() }.WithIcon(MenuParts.Repair);
        foreach (AppCommand? command in new AppCommand?[] { AppCommand.GlassLevels, AppCommand.KeepAreas, AppCommand.ToggleShrink, AppCommand.ToggleCrop, null, AppCommand.ToggleDarkTitleBar })
            effects.DropDownItems.Add(command is { } entry ? Entry(entry) : new ToolStripSeparator());
        items.Add(effects);

        items.Add(new ToolStripSeparator());
        ToolStripMenuItem arrange = Command(AppCommand.Arrange);
        PopulateArrange(arrange.DropDownItems);
        items.Add(arrange);
        var more = new ToolStripMenuItem(T("menu.more")) { Tag = new MoreGroup() }.WithIcon(MenuParts.More);
        more.DropDownItems.Add(Command(AppCommand.ShowWindowInfo));
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
        items.Add(new ToolStripSeparator());
        items.Add(Command(AppCommand.SaveWindowSetup));
        items.Add(Command(AppCommand.RestoreWindow));
    }

    // The slider, or the two steps where there is none, then default opacity.
    internal void PopulateOpacity(ToolStripItemCollection items)
    {
        if (OpacityPercent is not null && SetOpacityPercent is { } setOpacity)
            items.Add(new OpacityRow(T("pip.opacity"), percent => WithTarget(window => setOpacity(window, percent))));
        else
        {
            items.Add(Command(AppCommand.OpacityDown));
            items.Add(Command(AppCommand.OpacityUp));
        }
        items.Add(Command(AppCommand.ToggleDefaultOpacity));
    }

    // The grid of nine places, then the rest of CommandCatalog.ArrangeMenu.
    internal void PopulateArrange(ToolStripItemCollection items)
    {
        items.Add(new PlacementRow(place => WithTarget(window => run(place, window)), command => Hotkey?.Invoke(command)) { Tag = AppCommand.AlignCenter });
        items.Add(new ToolStripSeparator());
        if (MoveToDisplay is { } move)
        {
            var displays = new ToolStripMenuItem(T("placement.moveToDisplay")).WithIcon(ColorIcons.Displays);
            foreach (Screen screen in Screen.AllScreens)
            {
                string device = screen.DeviceName;
                displays.DropDownItems.Add($"{device} ({screen.Bounds.Width}×{screen.Bounds.Height})", null,
                    (_, _) => WithTarget(window => move(window, device))).Tag = new DisplayMove(device);
            }
            items.Add(displays);
        }
        if (ResizeTo is { } resize)
        {
            var sizes = new ToolStripMenuItem(T("placement.size")).WithIcon(ColorIcons.WindowSize);
            foreach (Size size in PresetSizes)
                sizes.DropDownItems.Add($"{size.Width} × {size.Height}", null,
                    (_, _) => WithTarget(window => resize(window, size))).Tag = new Resize(size);
            items.Add(sizes);
        }
        foreach (AppCommand? action in CommandCatalog.ArrangeMenu.SkipWhile(action => action is { } place && CommandCatalog.AnchorOf(place) is not null).Skip(1))
            items.Add(action is { } entry ? Entry(entry) : new ToolStripSeparator());
    }

    // Fill display asks which display when there are several; the list is made again each time the menu opens.
    private sealed class FillDisplay : ToolStripMenuItem
    {
        private readonly Action<Action<nint>> _withTarget;
        private readonly Action<nint, Rectangle> _fill;
        private readonly Action _stale;

        public FillDisplay(Action<Action<nint>> withTarget, Action<nint, Rectangle> fill, Action stale, string? keys)
            : base(CommandCatalog.Name(AppCommand.ToggleBorderlessFill), MenuParts.Icon(MenuParts.FullScreen))
        {
            (_withTarget, _fill, _stale) = (withTarget, fill, stale);
            Tag = AppCommand.ToggleBorderlessFill;
            ShortcutKeyDisplayString = keys is { Length: > 0 } ? keys : null;
            Click += (_, _) =>
            {
                Screen[] screens = Screen.AllScreens;
                if (screens.Length == 1) _withTarget(window => _fill(window, screens[0].Bounds));
            };
            DropDownOpening += (_, _) => ListDisplays();
            ListDisplays();
        }

        private string? _listed;

        // Refresh calls this while the menu stays open, so the list is only made again when the displays changed.
        public void ListDisplays()
        {
            Screen[] screens = Screen.AllScreens;
            string now = string.Join("|", screens.Select(screen => $"{screen.DeviceName} {screen.Bounds}"));
            if (now == _listed) return;
            _listed = now;
            while (DropDownItems.Count > 0) DropDownItems[0].Dispose();
            if (screens.Length < 2) return;
            foreach (Screen screen in screens)
                DropDownItems.Add(screen.DeviceName, null, (_, _) => _withTarget(window =>
                {
                    Screen? current = Screen.AllScreens.FirstOrDefault(item => item.DeviceName == screen.DeviceName);
                    if (current is null || current.Bounds != screen.Bounds) { _stale(); return; }
                    _fill(window, current.Bounds);
                }));
        }
    }

    internal static void LeaveOut(ToolStripItemCollection items, IReadOnlySet<AppCommand> onBar)
    {
        bool Shown(ToolStripItem item) => item switch
        {
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

    private ToolStripMenuItem Entry(AppCommand command) => CommandCatalog.IsExperimental(command) ? Command(command).Experimental() : Command(command);

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
            else if (entry is FillDisplay displays)
                displays.ListDisplays();
            if (entry is not ToolStripMenuItem item) continue;
            if (item.Tag is AppCommand command)
            {
                item.Available = available(command);
                item.Checked = window != 0 && isOn(command, window);
                item.Enabled = command == AppCommand.RestoreAll || window != 0 && enabled(command, window);
                MenuParts.ShowKeys(item, Hotkey?.Invoke(command));
            }
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
