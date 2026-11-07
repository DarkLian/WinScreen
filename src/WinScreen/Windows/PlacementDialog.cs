using static WinScreen.Settings.UiText;

namespace WinScreen.Windows;

/// <summary>Asks for a window's exact position and size, in screen pixels of its visible frame.</summary>
internal sealed class PlacementDialog : Form
{
    private readonly NumericUpDown _left = Number(-32768), _top = Number(-32768), _width = Number(1), _height = Number(1);

    public PlacementDialog(string title, Rectangle frame)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        _left.Value = frame.Left;
        _top.Value = frame.Top;
        _width.Value = frame.Width;
        _height.Value = frame.Height;

        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        void Row(string label, Control control)
        {
            table.Controls.Add(new Label { Text = T(label), AutoSize = true, Anchor = AnchorStyles.Left });
            table.Controls.Add(control);
        }

        Row("Left", _left);
        Row("Top", _top);
        Row("Width", _width);
        Row("Height", _height);
        var apply = new Button { Text = T("Apply"), DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        table.Controls.Add(apply);
        table.Controls.Add(cancel);
        AcceptButton = apply;
        CancelButton = cancel;
        Controls.Add(table);
    }

    public Rectangle Frame => new((int)_left.Value, (int)_top.Value, (int)_width.Value, (int)_height.Value);

    private static NumericUpDown Number(int lowest) => new() { Minimum = lowest, Maximum = 32767, Width = 90 };
}
