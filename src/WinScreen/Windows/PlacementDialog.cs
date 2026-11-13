using static WinScreen.Localization.UiText;

namespace WinScreen.Windows;

/// <summary>Asks for a window's exact position and size, in screen pixels of its visible frame.</summary>
internal sealed class PlacementDialog : Settings.ThemedDialog
{
    private readonly NumericUpDown _left = Number(-32768), _top = Number(-32768), _width = Number(1), _height = Number(1);

    public PlacementDialog(string title, Rectangle frame) : base(title)
    {
        _left.Value = frame.Left;
        _top.Value = frame.Top;
        _width.Value = frame.Width;
        _height.Value = frame.Height;
        Row(T("placement.field.left"), _left);
        Row(T("placement.field.top"), _top);
        Row(T("placement.field.width"), _width);
        Row(T("placement.field.height"), _height);
        Buttons(T("common.apply"), T("common.cancel"));
    }

    public Rectangle Frame => new((int)_left.Value, (int)_top.Value, (int)_width.Value, (int)_height.Value);

    private static NumericUpDown Number(int lowest) => new() { Minimum = lowest, Maximum = 32767, Width = 110 };
}
