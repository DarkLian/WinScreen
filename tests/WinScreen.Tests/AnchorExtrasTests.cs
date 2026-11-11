using System.Reflection;
using WinScreen.Anchors;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>How an Anchor's title is shortened: the icon already names the program.</summary>
public sealed class AnchorTitleTests
{
    [Theory]
    [InlineData("Inbox - Mail - Google Chrome", "chrome", "Google Chrome", "Inbox - Mail")]
    [InlineData("Pull requests - Microsoft​ Edge", "msedge", "Microsoft Edge", "Pull requests")]
    [InlineData("notes.txt - Notepad", "notepad", "Notepad", "notes.txt")]
    [InlineData("Report - Draft", "WINWORD", "Microsoft Word", "Report - Draft")]
    [InlineData("Discord", "Discord", "Discord", "Discord")]
    public void The_program_name_at_the_end_is_dropped(string title, string program, string description, string expected) =>
        Assert.Equal(expected, AnchorTitles.Short(title, program, description));

    [Fact]
    public void Previews_are_on_and_taskbar_buttons_stay_by_default()
    {
        var preferences = new AnchorPreferences().Validated();
        Assert.True(preferences.HoverPreview);
        Assert.False(preferences.OffTaskbar);
    }
}

[Trait("Category", "RealWindows")]
public sealed class AnchorExtrasTests
{
    private static AnchorButton[] Buttons(AnchorSet anchors) =>
        [.. ((Dictionary<nint, AnchorButton>)typeof(AnchorSet).GetField("_anchors",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(anchors)!).Values];

    /// <summary>
    /// A window with an Anchor the person added leaves the taskbar, and its button comes back whenever the Anchor
    /// can't be seen or goes: hidden, removed, or the choice turned off.
    /// </summary>
    [Fact]
    public void The_taskbar_button_is_away_only_while_its_Anchor_shows()
    {
        using var target = new TargetProcess(new Rectangle(240, 200, 300, 200));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var anchors = new AnchorSet();
            anchors.SetOffTaskbar = (window, off) => { if (off) windows.TakeOffTaskbar(window); else windows.PutBackOnTaskbar(window); };
            anchors.IsOffTaskbar = windows.IsOffTaskbar;
            try
            {
                anchors.Configure(new AnchorPreferences { OffTaskbar = true });
                anchors.Toggle(target.Handle);
                Assert.True(windows.IsOffTaskbar(target.Handle));
                anchors.Hidden = true;
                Assert.False(windows.IsOffTaskbar(target.Handle));
                anchors.Hidden = false;
                Assert.True(windows.IsOffTaskbar(target.Handle));
                anchors.Configure(new AnchorPreferences { OffTaskbar = false });
                Assert.False(windows.IsOffTaskbar(target.Handle));
                anchors.Configure(new AnchorPreferences { OffTaskbar = true });
                Assert.True(windows.IsOffTaskbar(target.Handle));
                anchors.Toggle(target.Handle);
                Assert.False(windows.IsOffTaskbar(target.Handle));
            }
            finally { windows.RestoreAll(); }
        });
    }

    /// <summary>A button taken away for an Anchor is in the record a crash leaves, so the next start puts it back.</summary>
    [Fact]
    public void A_taskbar_button_taken_for_an_Anchor_comes_back_after_a_crash()
    {
        using var target = new TargetProcess(new Rectangle(240, 200, 300, 200));
        string path = Path.Combine(Path.GetTempPath(), $"winscreen-anchor-taskbar-{Guid.NewGuid():N}.json");
        try
        {
            new WindowController([], new ChangeJournal(path)).TakeOffTaskbar(target.Handle);
            Assert.Contains("\"TaskbarButtonRemoved\":true", File.ReadAllText(path).Replace(" ", ""));
            var next = new WindowController([], new ChangeJournal(path));
            next.RecoverFromJournal();
            Assert.False(next.IsOffTaskbar(target.Handle));
            Assert.False(File.Exists(path)); // Nothing is left to put back, so the record is gone.
        }
        finally { File.Delete(path); }
    }

    /// <summary>Two Anchors with the same icon each show their window's title; an Anchor alone shows its icon only.</summary>
    [Fact]
    public void Lookalike_Anchors_show_their_titles()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var first = new Form { Text = "Budget - Sheet", ShowInTaskbar = false };
            using var second = new Form { Text = "Holiday plan", ShowInTaskbar = false };
            first.Show();
            second.Show();
            using var anchors = new AnchorSet();
            anchors.Toggle(first.Handle);
            AnchorButton alone = Assert.Single(Buttons(anchors));
            Assert.Null(alone.Title);
            int square = alone.Width;

            anchors.Toggle(second.Handle);
            AnchorButton[] both = Buttons(anchors);
            Assert.Equal(["Budget - Sheet", "Holiday plan"], both.Select(button => button.Title).Order());
            Assert.All(both, button => Assert.True(button.Width > square * 2));
            Assert.All(both, button => Assert.Equal(square, button.Height));

            anchors.Toggle(second.Handle);
            Assert.Null(Assert.Single(Buttons(anchors)).Title);
            Assert.Equal(square, alone.Width);
        });
    }

    /// <summary>The window's live picture comes up beside its Anchor after a rest, and goes when the pointer leaves.</summary>
    [Fact]
    public void Resting_on_an_Anchor_shows_its_window_beside_it()
    {
        using var target = new TargetProcess(new Rectangle(240, 200, 300, 200));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var anchors = new AnchorSet();
            anchors.Toggle(target.Handle);
            AnchorButton anchor = Assert.Single(Buttons(anchors));
            Point before = Cursor.Position;
            try
            {
                Cursor.Position = anchor.PointToScreen(new Point(anchor.Width / 2, anchor.Height / 2));
                typeof(AnchorButton).GetMethod("OnMouseEnter", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(anchor, [EventArgs.Empty]);
                Assert.True(Desktop.WaitFor(() => anchor.Previewing, 3000, () => { Application.DoEvents(); Thread.Sleep(20); }));
                Form preview = Application.OpenForms.Cast<Form>().Single(form => form.GetType().Name == "PipHoverPreview");
                Assert.False(preview.Bounds.IntersectsWith(anchor.Bounds));
                Assert.False(preview.Bounds.Contains(Cursor.Position));

                Cursor.Position = new Point(anchor.Left - 200, anchor.Top);
                Assert.True(Desktop.WaitFor(() => !anchor.Previewing, 3000, () => { Application.DoEvents(); Thread.Sleep(20); }));
            }
            finally { Cursor.Position = before; }
        });
    }
}
