using System.Runtime.CompilerServices;

namespace WinScreen.Settings;

/// <summary>
/// Makes a Settings page's full-width rows wider together, so a bigger window shows longer lines rather than empty
/// space. Each control's own width is kept the first time it is seen, and every stretch starts from that.
/// </summary>
internal static class PageStretch
{
    private static readonly ConditionalWeakTable<Control, StrongBox<int>> Usual = new();

    private static int UsualWidth(Control control, int now) => Usual.GetValue(control, _ => new StrongBox<int>(now)).Value;

    /// <param name="extra">Pixels to add to each full-width row.</param>
    /// <param name="full">The width of a full row at the usual size; anything about this wide counts as one.</param>
    /// <param name="rows">How many rows a list of programs shows before it scrolls.</param>
    internal static void Apply(Control container, int extra, int full, int rows)
    {
        int near = full - SettingsStyle.Unit(container, 16);
        foreach (Control child in container.Controls)
        {
            switch (child)
            {
                case SwitchBox { Compact: false } option:
                    option.Extra = extra;
                    break;
                case Label { MaximumSize.Width: > 0 } words:
                    int usual = UsualWidth(words, words.MaximumSize.Width);
                    if (usual >= near) words.MaximumSize = new Size(usual + extra, 0);
                    break;
                case ProgramList list:
                    list.MostRows = rows;
                    list.Width = UsualWidth(list, list.Width) + extra;
                    break;
                case { AutoSize: true }:
                    // Cards and groups of rows grow with what they hold, and a card holding only short parts, such as
                    // a row of buttons, keeps the full width all the same.
                    if (child.MinimumSize.Width > 0 && UsualWidth(child, child.MinimumSize.Width) is var least && least >= near)
                        child.MinimumSize = child.MinimumSize with { Width = least + extra };
                    Apply(child, extra, full, rows);
                    break;
                default:
                    int width = UsualWidth(child, child.Width);
                    if (width >= near) child.Width = width + extra; // Its parts are anchored to follow.
                    break;
            }
        }
    }
}
