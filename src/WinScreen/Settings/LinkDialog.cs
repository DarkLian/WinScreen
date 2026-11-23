using static WinScreen.Localization.UiText;

namespace WinScreen.Settings;

// A link to a picture or video for Glass to show, typed or pasted.
internal sealed class LinkDialog : ThemedDialog
{
    private readonly TextBox _link = new() { Width = 420 };

    private LinkDialog() : base(T("settings.backgrounds.link.title"))
    {
        Row(T("settings.backgrounds.link.label"), _link);
        Note(T("settings.backgrounds.link.help"));
        Buttons(T("common.add"), T("common.cancel"));
    }

    internal string Link => _link.Text.Trim();

    // The link, or null when nothing was added; one that isn't a web link is said so, and nothing is added.
    internal static string? Ask(IWin32Window owner)
    {
        using var dialog = new LinkDialog();
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Link.Length == 0) return null;
        if (Glass.BackdropChoice.IsLinkText(dialog.Link)) return dialog.Link;
        ThemedMessage.Show(owner, T("settings.backgrounds.link.invalid"), T("settings.backgrounds.link.title"));
        return null;
    }
}
