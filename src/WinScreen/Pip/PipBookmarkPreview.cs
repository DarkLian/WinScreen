using WinScreen.Settings;
using static WinScreen.Localization.UiText;

namespace WinScreen.Pip;

/// <summary>Confirms a bookmark against today's display layout, before capture starts.</summary>
internal sealed class PipBookmarkPreview : Form
{
    private readonly PipSourceBookmark _bookmark;
    private readonly ComboBox _display = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _description = new() { AutoSize = true, Dock = DockStyle.Top };
    private BookmarkDisplay[] _displays = [];
    internal ResolvedBookmark? Selection { get; private set; }

    internal PipBookmarkPreview(PipSourceBookmark bookmark)
    {
        _bookmark = bookmark;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = T("pip.bookmarks.preview") + " - " + bookmark.Name;
        Size = new Size(680, 480);
        MinimumSize = new Size(360, 280);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        var open = new Button { Text = T("common.open"), AutoSize = true };
        var cancel = new Button { Text = T("common.cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        buttons.Controls.AddRange([open, cancel]);
        Controls.AddRange([_picture, _description, _display, buttons]);
        AcceptButton = open;
        CancelButton = cancel;
        _display.AccessibleName = T("pip.sources.displays");
        _picture.AccessibleName = T("pip.bookmarks.resolvedRegion");
        _display.SelectedIndexChanged += (_, _) => UpdatePreview();
        Shown += (_, _) => RefreshDisplays();
        open.Click += (_, _) =>
        {
            if (Selection is not { } selected) return;
            Screen? current = Screen.AllScreens.FirstOrDefault(item => item.DeviceName == selected.Display.Device);
            if (current is null || current.Bounds != selected.Display.Bounds) { RefreshDisplays(); return; }
            DialogResult = DialogResult.OK;
            Close();
        };
    }

    private void RefreshDisplays()
    {
        _displays = Screen.AllScreens.Select(screen => new BookmarkDisplay(screen.DeviceName, screen.Bounds, screen.Primary)).ToArray();
        ResolvedBookmark? resolved = _bookmark.Resolve(_displays);
        _display.Items.Clear();
        _display.Items.AddRange(_displays.Select(display => (object)$"{display.Device} ({display.Bounds.Width}×{display.Bounds.Height})").ToArray());
        _display.SelectedIndex = resolved is null ? -1 : Array.FindIndex(_displays, display => display.Device == resolved.Display.Device);
    }

    private void UpdatePreview()
    {
        if (_display.SelectedIndex < 0) return;
        BookmarkDisplay display = _displays[_display.SelectedIndex];
        Selection = _bookmark.Resolve([display]);
        if (Selection is not { } selected) return;
        Rectangle region = selected.Region ?? new Rectangle(Point.Empty, display.Bounds.Size);
        _description.Text = T("pip.bookmarks.layoutNotice") + Environment.NewLine +
            $"{display.Device}: {region.X}, {region.Y} — {region.Width}×{region.Height} px";
        try
        {
            var image = new Bitmap(display.Bounds.Width, display.Bounds.Height);
            try
            {
                using Graphics graphics = Graphics.FromImage(image);
                graphics.CopyFromScreen(display.Bounds.Location, Point.Empty, display.Bounds.Size);
                using var pen = new Pen(Color.Red, Math.Max(3, image.Width / 300));
                graphics.DrawRectangle(pen, region.X, region.Y, Math.Max(1, region.Width - 1), Math.Max(1, region.Height - 1));
            }
            catch { image.Dispose(); throw; }
            Image? previous = _picture.Image;
            _picture.Image = image;
            previous?.Dispose();
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        { _description.Text += Environment.NewLine + failure.Message; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _picture.Image?.Dispose();
        base.Dispose(disposing);
    }
}
