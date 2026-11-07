using System.Reflection;
using WinScreen.Anchors;
using WinScreen.Native;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class AnchorTests
{
    private static void OnUiThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Middle_click_removes_anchor_even_when_target_cancels_close_or_hides(bool hide)
    {
        OnUiThread(() =>
        {
            using var target = new Form { Text = "Anchor close test", ShowInTaskbar = false };
            int closeRequests = 0;
            target.FormClosing += (_, e) =>
            {
                closeRequests++;
                e.Cancel = true;
                if (hide)
                    target.Hide();
            };
            target.Show();
            using var anchors = new AnchorSet();
            Assert.True(anchors.Toggle(target.Handle));
            var anchor = Assert.Single(Application.OpenForms.OfType<AnchorButton>());
            nint targetHandle = target.Handle;
            // Send the same mouse messages the native window receives, without moving the user's mouse.
            User32.PostMessage(anchor.Handle, 0x0207, 0x0010, (nint)((10 << 16) | 10));
            User32.PostMessage(anchor.Handle, 0x0208, 0, (nint)((10 << 16) | 10));
            PumpUntil(() => anchor.IsDisposed && closeRequests == 1);
            Assert.True(anchor.IsDisposed);
            Assert.False(anchors.Has(targetHandle));
            Assert.True(User32.IsWindow(targetHandle));
            Assert.Equal(1, closeRequests);
            Assert.True(anchors.Toggle(targetHandle));
            Assert.Single(Application.OpenForms.OfType<AnchorButton>());
        });
    }

    [Fact]
    public void Remove_only_action_leaves_target_open_and_can_be_added_again()
    {
        OnUiThread(() =>
        {
            using var target = new Form { Text = "Anchor remove test", ShowInTaskbar = false };
            int closeRequests = 0;
            target.FormClosing += (_, _) => closeRequests++;
            target.Show();
            using var anchors = new AnchorSet();
            anchors.Toggle(target.Handle);
            var anchor = Assert.Single(Application.OpenForms.OfType<AnchorButton>());
            var menu = (ContextMenuStrip)typeof(AnchorButton).GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(anchor)!;
            ((ToolStripMenuItem)menu.Items[^1]).PerformClick();
            Assert.True(anchor.IsDisposed);
            Assert.False(anchors.Has(target.Handle));
            Assert.Equal(0, closeRequests);
            Assert.True(target.Visible);
            Assert.True(anchors.Toggle(target.Handle));
        });
    }

    private static void PumpUntil(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}
