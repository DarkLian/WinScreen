using System.Reflection;
using WinScreen.Hotkeys;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class WorkflowRegressionTests
{
    [Fact]
    public void A_disk_save_failure_releases_reserved_shortcuts_and_keeps_live_settings()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-failed-save-").FullName;
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                string path = Path.Combine(folder, "settings.json");
                var settings = new AppSettings { WelcomeShown = true, ShowNotifications = false, ShowToolbar = false,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => "") };
                using var app = new TrayApplication(new WindowController([]), settings, path);
                try
                {
                    File.Delete(path);
                    Directory.CreateDirectory(path); // The atomic file replacement must fail.
                    var draft = settings.Clone();
                    draft.ShowNotifications = true;
                    draft.Hotkeys[AppCommand.TogglePin] = "Ctrl+Shift+F19";
                    var failure = Assert.Throws<TargetInvocationException>(() => typeof(TrayApplication)
                        .GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [draft]));
                    Assert.True(failure.InnerException is IOException or UnauthorizedAccessException);
                    Assert.False(settings.ShowNotifications);
                    Assert.Empty(settings.Hotkeys[AppCommand.TogglePin]);
                    using var other = new HotkeyHost();
                    Assert.True(other.TryRegister(101, 2 | 4, Keys.F19, out string? reason), reason);
                }
                finally { app.ExitThread(); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Disabled_experimental_shortcuts_are_released_and_reenable_conflicts_are_atomic()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-shortcuts-").FullName;
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var settings = new AppSettings { WelcomeShown = true, ShowNotifications = false, ShowToolbar = false,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => "") };
                settings.Hotkeys[AppCommand.ToggleDark] = "Ctrl+Shift+F20";
                using var app = new TrayApplication(new WindowController([]), settings, Path.Combine(folder, "settings.json"));
                using var other = new HotkeyHost();
                try
                {
                    Assert.True(other.TryRegister(100, 2 | 4, Keys.F20, out string? failure), failure);
                    var enabled = settings.Clone();
                    enabled.ExperimentalFeatures = true;
                    var problems = (IReadOnlyList<HotkeyProblem>)typeof(TrayApplication)
                        .GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [enabled])!;
                    Assert.Equal(AppCommand.ToggleDark, Assert.Single(problems).Command);
                    Assert.False(settings.ExperimentalFeatures);
                    Assert.Equal("Ctrl+Shift+F20", settings.Hotkeys[AppCommand.ToggleDark]);
                    Assert.False(AppSettings.Load(Path.Combine(folder, "settings.json")).ExperimentalFeatures);
                }
                finally { app.ExitThread(); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_recovery_lock_timeout_never_reads_or_changes_the_journal()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-lock-").FullName;
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using Mutex mutex = RecoveryWatcher.ForRecovery(out bool held);
            Assert.True(held);
            ready.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        ready.Wait();
        try
        {
            string path = Path.Combine(folder, "journal.json");
            File.WriteAllText(path, "deliberately invalid journal");
            var controller = new WindowController([], new ChangeJournal(path));
            controller.RecoverFromJournal();
            Assert.True(controller.RecoveryBlocked);
            Assert.Equal("deliberately invalid journal", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { release.Set(); holder.Join(); Directory.Delete(folder, true); }
    }
}

[Trait("Category", "RealWindows")]
public sealed class ConcealmentOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_restore_keeps_the_view_mapping_until_a_retry_reveals_the_source(bool shrink)
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-retry-").FullName;
        try
        {
            using var target = new Support.TargetProcess(new Rectangle(200, 200, 400, 300));
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ExperimentalFeatures = true, ShowToolbar = false,
                    ShowNotifications = false, WelcomeShown = true,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => "") };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"));
                try
                {
                    typeof(TrayApplication).GetMethod(shrink ? "Shrink" : "OpenViewOf", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(app, [target.Handle]);
                    var mapping = (System.Collections.IDictionary)typeof(TrayApplication)
                        .GetField(shrink ? "_shrunk" : "_pips", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
                    if (!shrink) ((WinScreen.Pip.PipWindow)mapping[target.Handle]!).SetOriginalHidden(true);
                    target.Hang();
                    var run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    run.Invoke(app, [AppCommand.RestoreWindow, target.Handle]);
                    Assert.True(mapping.Contains(target.Handle));
                    Assert.True(controller.NeedsRecoveryWatcher);
                    target.Unhang();
                    run.Invoke(app, [AppCommand.RestoreWindow, target.Handle]);
                    Assert.False(mapping.Contains(target.Handle));
                    Assert.False(controller.IsHidden(target.Handle));
                    Assert.False(WindowInfo.IsConcealed(target.Handle));
                }
                finally { app.ExitThread(); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Only_the_owner_can_reveal_and_chosen_opacity_survives_concealment()
    {
        using var target = new Support.TargetProcess(new Rectangle(200, 200, 400, 300));
        var controller = new WindowController([]);
        object first = new(), second = new();
        try
        {
            controller.SetOpacity(target.Handle, 128);
            controller.HideOwned(target.Handle, first);
            Assert.Equal(128, controller.OpacityOf(target.Handle));
            controller.Park(target.Handle);
            controller.SetParkedReachable(target.Handle, true); // A visit makes it faint for a while.
            controller.SetParkedReachable(target.Handle, false);
            controller.Unpark(target.Handle);
            Assert.True(controller.IsHidden(target.Handle));
            Assert.Throws<WindowOperationException>(() => controller.HideOwned(target.Handle, second));
            Assert.Throws<WindowOperationException>(() => controller.ShowOwned(target.Handle, second));
            Assert.True(controller.NeedsRecoveryWatcher);
            controller.ShowOwned(target.Handle, first);
            Assert.False(controller.NeedsRecoveryWatcher);
            Assert.Equal(128, controller.OpacityOf(target.Handle));
        }
        finally { controller.RestoreAll(); }
    }
}
