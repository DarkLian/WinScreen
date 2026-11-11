using static WinScreen.Localization.UiText;

namespace WinScreen.Settings;

/// <summary>
/// A small window of facts to read and copy, such as a view's diagnostics or a window's details, in the same look as
/// Settings. It opens in front and leaves everything else usable: it never waits to be closed, so a view stays
/// usable while it is open, and several can be open at once.
/// </summary>
internal sealed class InfoWindow : Form
{
    private static readonly Font TitleFont = new(SettingsStyle.PageTitleFont.FontFamily, 15f);

    /// <param name="title">What the facts are about, as the window's title and heading.</param>
    /// <param name="text">The facts, a line each.</param>
    /// <param name="actions">Buttons beside Close, such as Copy; each with whether it can be used.</param>
    internal InfoWindow(string title, string text, params (string Text, Action Click, bool Enabled)[] actions)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FocusVisuals.Watch();
        Text = title;
        Icon = Tray.TrayApplication.LoadIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 440);
        MinimumSize = new Size(420, 280);
        MinimizeBox = false;
        TopMost = true; // In front of views and pinned windows, which are on top too.
        Font = SettingsStyle.BodyFont;
        BackColor = SettingsStyle.Page;
        ForeColor = SettingsStyle.Text;
        Padding = new Padding(20, 14, 20, 16);

        var heading = new SoftLabel { Text = title, Font = TitleFont, AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false,
            Padding = new Padding(0, 0, 0, 12) };
        var box = new TextBox
        {
            Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), Multiline = true, ReadOnly = true,
            BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.None, WordWrap = true, Dock = DockStyle.Fill,
            BackColor = SettingsStyle.Card, ForeColor = SettingsStyle.Text, TabStop = true,
        };
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 6, 6) };
        card.Controls.Add(box);
        // A scroll bar only while the facts don't all fit; long lines wrap instead of scrolling sideways.
        box.Resize += (_, _) =>
        {
            int needs = TextRenderer.MeasureText(box.Text, box.Font, new Size(box.ClientSize.Width, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            ScrollBars wanted = needs > box.ClientSize.Height ? ScrollBars.Vertical : ScrollBars.None;
            if (box.ScrollBars != wanted) box.ScrollBars = wanted;
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(0, 12, 0, 0), BackColor = SettingsStyle.Page };
        var close = new PlainButton { Text = T("common.close"), Primary = true, Margin = new Padding(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        foreach ((string label, Action click, bool enabled) in actions)
        {
            var button = new PlainButton { Text = label, Enabled = enabled, Margin = new Padding(8, 0, 0, 0) };
            button.Click += (_, _) => click();
            buttons.Controls.Add(button);
        }
        CancelButton = close;

        Controls.Add(card);
        Controls.Add(buttons);
        Controls.Add(heading);
        ResumeLayout(true);
        // The facts aren't all selected, as a text box would have them at first.
        Shown += (_, _) => { box.SelectionStart = 0; box.SelectionLength = 0; close.Focus(); };
    }

    /// <summary>Opens it in front, without waiting for it to close.</summary>
    internal void ShowInFront()
    {
        Show();
        Activate();
        Windows.TopLayer.Raise(Handle);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;
        int dark = Tray.MenuStyle.Dark ? 1 : 0, caption = ColorRef(SettingsStyle.Page), text = ColorRef(SettingsStyle.Text);
        Native.Dwmapi.DwmSetWindowAttribute(Handle, Native.Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, in dark, sizeof(int));
        Native.Dwmapi.DwmSetWindowAttribute(Handle, Native.Dwmapi.DWMWA_CAPTION_COLOR, in caption, sizeof(int));
        Native.Dwmapi.DwmSetWindowAttribute(Handle, Native.Dwmapi.DWMWA_TEXT_COLOR, in text, sizeof(int));
    }
}
