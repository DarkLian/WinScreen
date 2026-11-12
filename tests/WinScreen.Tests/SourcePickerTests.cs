using WinScreen.Pip;
using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

/// <summary>What the parts of Choose what to show do, now that region and removal sit where they belong.</summary>
[Trait("Category", "RealWindows")]
public sealed class SourcePickerTests
{
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void Pump() { for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(20); } }

    /// <summary>Region is one of the kinds of thing to show: its main button drags out part of the chosen display.</summary>
    [Fact]
    public void Choosing_Region_makes_the_main_button_drag_out_part_of_the_display()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var picker = new PipSourcePicker(_ => true);
            string? wanted = null;
            picker.RegionWanted += device => wanted = device;
            picker.Show();
            Pump();
            ComboBox kind = Descendants(picker).OfType<ComboBox>().First(box => box.Items.Count == 4);
            kind.SelectedIndex = 2;
            Pump();
            ListView list = Descendants(picker).OfType<ListView>().Single();
            if (list.SelectedItems.Count == 0) list.Items[0].Selected = true;
            Button main = (Button)picker.AcceptButton!;
            Assert.Equal(WinScreen.Localization.UiText.T("pip.sources.selectRegion"), main.Text);
            Assert.Contains(Descendants(picker).OfType<Button>(), button => button.Visible && button.Enabled &&
                button.Text == WinScreen.Localization.UiText.T("pip.sources.exactArea"));
            main.PerformClick();
            Pump();
            Assert.NotNull(wanted);
            Assert.True(picker.IsDisposed);
        });
    }

    /// <summary>
    /// With few rows, Windows draws a line at the column's end down the empty space under them; the column reaches
    /// the list's far edge so that line stays out of sight, past the visible part of the list.
    /// </summary>
    [Fact]
    public void No_line_shows_under_the_rows_when_only_a_few_are_listed()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var few = new HashSet<nint>();
            using var picker = new PipSourcePicker(window => few.Contains(window) || (few.Count < 2 && few.Add(window)));
            picker.Show();
            Pump();
            ListView list = Descendants(picker).OfType<ListView>().Single();
            Assert.True(list.Columns[0].Width >= list.ClientSize.Width,
                $"column {list.Columns[0].Width}, list {list.ClientSize.Width}");
            Assert.True(list.Columns[0].Width >= list.Parent!.ClientSize.Width, "the column's end shows inside the list");
        });
    }

    /// <summary>A saved source is forgotten from its own row: Delete on the chosen one.</summary>
    [Fact]
    public void Delete_forgets_the_chosen_saved_source()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var saved = new List<PipSourceBookmark> { new() { Name = "Notes", Device = @"\\.\DISPLAY1" }, new() { Name = "Chat", Device = @"\\.\DISPLAY1" } };
            using var picker = new PipSourcePicker(_ => true) { Bookmarks = () => saved };
            picker.BookmarkRemoved += name => saved.RemoveAll(bookmark => bookmark.Name == name);
            picker.Show();
            Pump();
            Descendants(picker).OfType<ComboBox>().First(box => box.Items.Count == 4).SelectedIndex = 3;
            Pump();
            ListView list = Descendants(picker).OfType<ListView>().Single();
            list.Items[1].Selected = true;
            list.Focus();
            Native.User32.SendMessage(list.Handle, 0x0100 /* WM_KEYDOWN */, (nint)Keys.Delete, 0);
            Pump();
            Assert.Equal(["Notes"], saved.Select(bookmark => bookmark.Name));
            Assert.Single(list.Items.Cast<ListViewItem>());
            picker.Close();
        });
    }
}
