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

    public void Populate(ToolStripItemCollection items, bool tray)
    {
        foreach (AppCommand command in CommandCatalog.Ordered)
        {
            if (command == AppCommand.RestoreAll || !tray && command == AppCommand.RestoreWindow) continue;
            ToolStripMenuItem item = Command(command);
            if (command == AppCommand.Arrange) PopulateArrange(item.DropDownItems);
            items.Add(item);
            if (CommandCatalog.EndsGroup(command)) items.Add(new ToolStripSeparator());
        }
        items.Add(Command(AppCommand.ToggleDarkTitleBar));
        var borderless = new ToolStripMenuItem(T("window.borderless"));
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
        var copy = new ToolStripMenuItem(T("common.copy"));
        foreach (var (label, index) in new[] { (T("window.info.title"), 0), (T("window.info.hwnd"), 3), (T("window.info.processId"), 4), (T("window.info.path"), 2) })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => WithTarget(window =>
                WindowUtilities.CopyText(WindowInfo.Facts(window)[index].Value))) { Tag = new Utility(index == 2) };
            copy.DropDownItems.Add(item);
        }
        copy.DropDownItems.Add(T("window.copy.all"), null, (_, _) => WithTarget(window =>
            WindowUtilities.CopyText(WindowUtilities.Information(window)))).Tag = new Utility(false);
        items.Add(copy);
        items.Add(T("window.openLocation"), null, (_, _) => WithTarget(WindowUtilities.OpenLocation)).Tag = new Utility(true);
        if (!tray) items.Add(Command(AppCommand.RestoreWindow));
    }

    internal void PopulateArrange(ToolStripItemCollection items)
    {
        foreach (AppCommand? action in CommandCatalog.ArrangeMenu)
            items.Add(action is { } entry ? Command(entry) : new ToolStripSeparator());
        if (MoveToDisplay is { } move)
        {
            var displays = new ToolStripMenuItem(T("placement.moveToDisplay"));
            foreach (Screen screen in Screen.AllScreens)
            {
                string device = screen.DeviceName;
                displays.DropDownItems.Add($"{device} ({screen.Bounds.Width}×{screen.Bounds.Height})", null,
                    (_, _) => WithTarget(window => move(window, device))).Tag = new DisplayMove(device);
            }
            items.Insert(10, displays);
        }
    }

    private ToolStripMenuItem Command(AppCommand command) => new(CommandCatalog.Name(command), null, (_, _) =>
    {
        if (command == AppCommand.RestoreAll) run(command, 0);
        else WithTarget(window => run(command, window));
    }) { Tag = command };

    private void WithTarget(Action<nint> action)
    {
        if (target() is not { } identity || !(identity.Token == 0 ? identity.IsSameWindow() : identity.Matches())) { staleTarget(); return; }
        action(identity.Window);
    }

    public void Refresh(ToolStripItemCollection items)
    {
        nint window = target()?.Window ?? 0;
        foreach (ToolStripMenuItem item in items.OfType<ToolStripMenuItem>())
        {
            if (item.Tag is AppCommand command)
            {
                item.Visible = available(command);
                item.Checked = window != 0 && isOn(command, window);
                item.Enabled = command == AppCommand.RestoreAll || window != 0 && enabled(command, window);
            }
            else if (item.Tag is Utility utility)
                item.Enabled = window != 0 && (!utility.NeedsPath || WindowInfo.ExecutablePath(window).Length > 0);
            else if (item.Tag is DisplayMove display)
                item.Enabled = window != 0 && enabled(AppCommand.MoveToNextDisplay, window) &&
                    Screen.AllScreens.Any(screen => screen.DeviceName == display.Device);
            if (item.HasDropDownItems) Refresh(item.DropDownItems);
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
