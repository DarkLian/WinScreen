using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class PipLayeringTests
{
    private static void Pump(int ms)
    {
        long end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(5); }
    }

    [Fact]
    public void An_open_view_menu_needs_no_repeated_lifts_over_two_seconds()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 700, 500));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(900, 250);
            view.SetInteractive(true);
            view.ContextMenuStrip!.AutoClose = false;
            view.ContextMenuStrip.Show(view, new Point(30, 30));
            Pump(300);
            TopLayer.KeepAbove();
            long before = TopLayer.LiftCount;
            long end = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < end) { TopLayer.KeepAbove(); Pump(50); }
            Assert.Equal(before, TopLayer.LiftCount);
            Assert.Null(view.LiveInteraction);
            Assert.NotEqual(0, Desktop.ExStyle(view.Handle) & User32.WS_EX_TRANSPARENT);
            view.ContextMenuStrip.Close();
            Pump(100);
            Assert.Equal(0, Desktop.ExStyle(view.Handle) & User32.WS_EX_TRANSPARENT);
        });
    }

    [Theory]
    [InlineData("NotifyIconOverflowWindow")]
    [InlineData("TopLevelWindowForOverflowXamlIsland")]
    public void A_visible_overflow_panel_blocks_visits_and_stays_above_new_views(string className)
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 700, 500));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var panel = new ShellPanel(className, new Rectangle(850, 200, 650, 500));
            using var view = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(900, 250);
            view.SetInteractive(true);
            Pump(200);
            Assert.True(TopLayer.InputBlocked);
            typeof(Control).GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(view, [new MouseEventArgs(MouseButtons.None, 0, 60, 60, 0)]);
            Assert.Null(view.LiveInteraction);
            Assert.NotEqual(0, Desktop.ExStyle(view.Handle) & User32.WS_EX_TRANSPARENT);
            var point = new Point(view.Left + 100, view.Top + 100);
            Assert.Equal(panel.Handle, User32.GetAncestor(Desktop.TopWindowAt(point), User32.GA_ROOT));
            long lifts = TopLayer.LiftCount;
            for (int i = 0; i < 10; i++) { TopLayer.KeepAbove(); Pump(30); }
            Assert.Equal(lifts, TopLayer.LiftCount);
            panel.Hide();
            Pump(150);
            Assert.False(TopLayer.InputBlocked);
            Assert.Equal(0, Desktop.ExStyle(view.Handle) & User32.WS_EX_TRANSPARENT);
        });
    }

    [Fact]
    [Trait("Category", "Interactive")]
    public void Opening_overflow_ends_an_existing_visit_and_preserves_the_view_position()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 700, 500));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(900, 250);
            view.SetInteractive(true);
            Rectangle before = view.Bounds;
            Point home = Desktop.CursorPosition();
            Exception? failure = null;
            ShellPanel? panel = null;
            var driver = new Thread(() =>
            {
                try
                {
                    Point pointer = new(view.Left + 150, view.Top + 150);
                    Desktop.SlideMouse(pointer);
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null));
                    view.Invoke(() => panel = new ShellPanel("NotifyIconOverflowWindow", before));
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction is null));
                    Assert.NotEqual(0, Desktop.ExStyle(view.Handle) & User32.WS_EX_TRANSPARENT);
                    Assert.Equal(panel!.Handle, User32.GetAncestor(Desktop.TopWindowAt(pointer), User32.GA_ROOT));
                    Assert.Equal(before, view.Bounds);
                    view.Invoke(() => panel.Hide());
                    Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(view.Handle) & User32.WS_EX_TRANSPARENT) == 0));
                }
                catch (Exception e) { failure = e; }
                finally { Desktop.MoveMouse(home); }
            });
            driver.Start();
            while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            panel?.Dispose();
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        });
    }

    [Fact]
    [Trait("Category", "Interactive")]
    public void A_quick_right_click_on_the_title_bar_opens_the_menu_during_a_visit()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 700, 500));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(900, 250);
            view.SetInteractive(true);
            Point home = Desktop.CursorPosition();
            Exception? failure = null;
            var driver = new Thread(() =>
            {
                try
                {
                    Desktop.SlideMouse(new Point(view.Left + 150, view.Top + 150));
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null && view.TitleBarBounds is not null));
                    Thread.Sleep(180);
                    Rectangle bar = view.TitleBarBounds!.Value;
                    Desktop.SlideMouse(new Point(bar.Left + 80, bar.Top + bar.Height / 2));
                    mouse_event(8, 0, 0, 0, 0);
                    mouse_event(16, 0, 0, 0, 0);
                    Assert.True(Desktop.WaitFor(() => view.ContextMenuStrip!.Visible), "The title-bar press was lost.");
                    Assert.Null(view.LiveInteraction);
                    view.Invoke(() => view.ContextMenuStrip!.Close());
                }
                catch (Exception e) { failure = e; }
                finally { Desktop.MoveMouse(home); }
            });
            driver.Start();
            while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        });
    }

    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    private sealed class ShellPanel : IDisposable
    {
        private readonly string _name;
        private readonly nint _instance = GetModuleHandle(null);
        private readonly WindowProc _proc = DefWindowProc;
        internal nint Handle { get; }

        internal ShellPanel(string name, Rectangle bounds)
        {
            _name = name;
            var windowClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(_proc), Instance = _instance, ClassName = name };
            Assert.NotEqual((ushort)0, RegisterClassEx(ref windowClass));
            Handle = CreateWindowEx(0x08000088, name, "WinScreen test overflow", 0x90000000,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0, 0, _instance, 0);
            Assert.NotEqual(0, Handle);
        }
        internal void Hide() => User32.ShowWindow(Handle, 0);
        public void Dispose()
        {
            DestroyWindow(Handle);
            UnregisterClass(_name, _instance);
            GC.KeepAlive(_proc);
        }
        private delegate nint WindowProc(nint window, uint message, nint wp, nint lp);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size, Style;
            public nint Procedure;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background;
            public string? MenuName;
            public string ClassName;
            public nint SmallIcon;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string name,
            string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nint wp, nint lp);
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    }
}
