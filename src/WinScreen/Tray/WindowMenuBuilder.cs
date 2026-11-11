using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Settings;
using WinScreen.Windows;
using static WinScreen.Localization.UiText;

namespace WinScreen.Tray;

/// <summary>One command list and target check for the tray and pointer menus.</summary>
internal sealed class WindowMenuBuilder(Func<WindowIdentity?> target, Action<AppCommand, nint> run,
    Action<nint, Rectangle> fill, Func<AppCommand, bool> available, Func<AppCommand, nint, bool> enabled,
    Func<AppCommand, nint, bool> isOn, Action staleTarget)
{
    private sealed record Utility(bool NeedsPath);
    private sealed record DisplayMove(string Device);
    internal Action<nint, string>? MoveToDisplay { get; init; }

    /// <summary>The hotkey to show beside a command, or empty.</summary>
    internal Func<AppCommand, string>? Hotkey { get; init; }

    /// <summary>The window's opacity in percent, and the way to set it; without both there is no slider.</summary>
    internal Func<nint, int>? OpacityPercent { get; init; }
    internal Action<nint, int>? SetOpacityPercent { get; init; }

    private sealed record Group;

    public void Populate(ToolStripItemCollection items, bool tray)
    {
        items.Add(Command(AppCommand.TogglePin));
        if (OpacityPercent is not null && SetOpacityPercent is { } setOpacity)
            items.Add(new OpacityRow(T("pip.opacity"), percent => WithTarget(window => setOpacity(window, percent))));
        items.Add(Command(AppCommand.ToggleClickThrough));

        items.Add(MenuParts.Header(T("menu.group.elsewhere")));
        items.Add(Command(AppCommand.OpenPip));
        items.Add(Command(AppCommand.ToggleAnchor));

        items.Add(MenuParts.Header(T("menu.group.place")));
        items.Add(new PlacementRow(place => WithTarget(window => run(place, window)), command => Hotkey?.Invoke(command)) { Tag = AppCommand.AlignCenter });
        ToolStripMenuItem arrange = Command(AppCommand.Arrange);
        PopulateArrange(arrange.DropDownItems, places: false); // The grid above already offers the nine places.
        items.Add(arrange);

        items.Add(MenuParts.Header(T("menu.group.look")));
        items.Add(Command(AppCommand.ToggleDarkTitleBar));
        var borderless = new ToolStripMenuItem(T("window.borderless")).WithIcon(MenuParts.FullScreen);
        borderless.DropDownItems.Add(Command(AppCommand.ToggleBorderless));
        var fillItem = new ToolStripMenuItem(T("window.fillDisplay"), null, (_, _) =>
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
        // Effects that are still experimental sit together, and the whole entry goes when none is turned on in Settings.
        var experimental = new ToolStripMenuItem(T("menu.effects")) { Tag = new Group() }.WithIcon(MenuParts.Repair).Experimental();
        foreach (AppCommand command in new[] { AppCommand.ToggleDark, AppCommand.ToggleGlass, AppCommand.ToggleShrink, AppCommand.ToggleCrop })
            experimental.DropDownItems.Add(Command(command));
        items.Add(experimental);

        items.Add(new ToolStripSeparator());
        var more = new ToolStripMenuItem(T("menu.more")).WithIcon(MenuParts.More);
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

    /// <summary>
    /// The Arrange commands. With <paramref name="places"/> off the nine corners, edges and centre are left out, for a
    /// menu that shows the placement grid; the commands and their shortcuts stay the same.
    /// </summary>
    internal void PopulateArrange(ToolStripItemCollection items, bool places = true)
    {
        IEnumerable<AppCommand?> actions = places ? CommandCatalog.ArrangeMenu
            : CommandCatalog.ArrangeMenu.SkipWhile(action => action is { } entry && CommandCatalog.AnchorOf(entry) is not null).Skip(1);
        foreach (AppCommand? action in actions)
            items.Add(action is not { } entry ? new ToolStripSeparator()
                : CommandCatalog.IsExperimental(entry) ? Command(entry).Experimental() : Command(entry));
        if (MoveToDisplay is { } move)
        {
            var displays = new ToolStripMenuItem(T("placement.moveToDisplay"));
            foreach (Screen screen in Screen.AllScreens)
            {
                string device = screen.DeviceName;
                displays.DropDownItems.Add($"{device} ({screen.Bounds.Width}×{screen.Bounds.Height})", null,
                    (_, _) => WithTarget(window => move(window, device))).Tag = new DisplayMove(device);
            }
            items.Insert(places ? 10 : 0, displays);
        }
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
            else if (item.Tag is Utility utility)
                item.Enabled = window != 0 && (!utility.NeedsPath || WindowInfo.ExecutablePath(window).Length > 0);
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
        catch (ExternalException) { /* The person can retry when the clipboard is free. */ }
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
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
