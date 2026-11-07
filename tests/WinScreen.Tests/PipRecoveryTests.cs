using System.Runtime.InteropServices;
using WinScreen.Anchors;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class PipRecoveryTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(nint window, string text);

    [Fact]
    public void A_view_follows_changes_to_the_sources_title()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var pip = new PipWindow(target.Handle, new WindowController([]));
            pip.ShowOnTop();
            Assert.True(SetWindowText(target.Handle, "A different document"));
            Assert.True(Desktop.WaitFor(() => pip.Text.EndsWith("A different document"), 3000,
                Application.DoEvents), pip.Text);
        });
    }

    [Fact]
    public void An_anchor_reveals_a_hidden_view_source_before_activating_it()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var pip = new PipWindow(target.Handle, windows);
            using var anchors = new AnchorSet();
            anchors.ShowRequested += _ => pip.GoToSource();
            try
            {
                pip.ShowOnTop();
                pip.SetOriginalHidden(true);
                Assert.True(windows.IsHidden(target.Handle));
                anchors.Toggle(target.Handle);
                var anchor = Assert.Single(Application.OpenForms.OfType<AnchorButton>());
                // Native messages exercise the Anchor without moving or clicking the desktop pointer.
                User32.PostMessage(anchor.Handle, 0x0201, 1, (nint)((10 << 16) | 10));
                User32.PostMessage(anchor.Handle, 0x0202, 0, (nint)((10 << 16) | 10));
                Assert.True(Desktop.WaitFor(() => !windows.IsHidden(target.Handle), 3000, Application.DoEvents));
                Assert.False(WindowInfo.IsConcealed(target.Handle));
            }
            finally { windows.RestoreAll(); }
        });
    }

    [Fact]
    public void A_failed_anchor_reveal_does_not_focus_the_hidden_source()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var pip = new PipWindow(target.Handle, windows);
            pip.ShowOnTop();
            pip.SetOriginalHidden(true);
            User32.SetForegroundWindow(pip.Handle);
            target.Hang();
            pip.GoToSource();
            Assert.True(windows.IsHidden(target.Handle));
            Assert.NotEqual(target.Handle, User32.GetForegroundWindow());
        });
    }
}
