using static WinScreen.Localization.UiText;

namespace WinScreen.Settings;

internal class ThemedDialog : Form
{
    private readonly TableLayoutPanel _table = new() { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = SettingsStyle.Page };
    private readonly FlowLayoutPanel _buttons = new() { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
        Dock = DockStyle.Bottom, Padding = new Padding(0, 14, 0, 0), BackColor = SettingsStyle.Page };

    protected ThemedDialog(string title)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FocusVisuals.Watch();
        Text = title;
        Icon = Tray.TrayApplication.LoadIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true; // In front of views and pinned windows, which are on top too.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SettingsStyle.BodyFont;
        BackColor = SettingsStyle.Page;
        ForeColor = SettingsStyle.Text;
        Padding = new Padding(20, 16, 20, 16);
        var body = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill,
            BackColor = SettingsStyle.Page };
        body.Controls.Add(_table);
        body.Controls.Add(_buttons);
        Controls.Add(body);
        ResumeLayout(false);
    }

    protected void Row(string label, Control control)
    {
        _table.Controls.Add(new SoftLabel { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = false,
            Margin = new Padding(0, 6, 16, 6) });
        _table.Controls.Add(Framed(control));
    }

    protected void Whole(Control control)
    {
        control.Margin = new Padding(0, 6, 0, 6);
        Control added = Framed(control);
        _table.Controls.Add(added);
        _table.SetColumnSpan(added, 2);
    }

    protected void Note(string text) =>
        Whole(new SoftLabel { Text = text, AutoSize = true, UseMnemonic = false, ForeColor = SettingsStyle.Secondary, MaximumSize = new Size(560, 0) });

    private static Control Framed(Control control) => control switch
    {
        TextBox or NumericUpDown => new InputFrame(control) { Margin = new Padding(0, 4, 0, 4) },
        CheckBox box => Themed(box),
        _ => control,
    };

    private static CheckBox Themed(CheckBox box)
    {
        box.ForeColor = SettingsStyle.Text;
        box.BackColor = SettingsStyle.Page;
        return box;
    }

    protected void Buttons(string accept, string? cancel = null)
    {
        var ok = new PlainButton { Text = accept, Primary = true, DialogResult = DialogResult.OK, Margin = new Padding(8, 0, 0, 0) };
        AcceptButton = ok;
        if (cancel is not null)
        {
            var no = new PlainButton { Text = cancel, DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };
            CancelButton = no;
            _buttons.Controls.Add(no);
        }
        else CancelButton = ok;
        _buttons.Controls.Add(ok);
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

internal sealed class ThemedMessage : ThemedDialog
{
    private ThemedMessage(string title, string text, bool okCancel) : base(title)
    {
        Whole(new SoftLabel { Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(520, 0) });
        Buttons(T("common.ok"), okCancel ? T("common.cancel") : null);
    }

    internal static DialogResult Show(IWin32Window? owner, string text, string title, bool okCancel = false)
    {
        using var message = new ThemedMessage(title, text, okCancel);
        DialogResult result = owner is null ? message.ShowDialog() : message.ShowDialog(owner);
        return result == DialogResult.OK ? DialogResult.OK : DialogResult.Cancel;
    }
}
