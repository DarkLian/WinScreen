namespace WinScreen.Pip;

/// <summary>
/// A view shows on every virtual desktop, but many apps stop drawing while their window's desktop isn't shown, so the
/// view would freeze. While the person is on another desktop the view hides its original for now, which keeps it
/// drawing, and shows it again once they are back on its desktop, where Windows puts it back. A temporary hide, never
/// the person's own Keep hidden.
/// </summary>
internal static class AwayFromDesktop
{
    internal enum Step { Nothing, Hide, ShowAgain }

    /// <param name="awayFrom">The original's desktop while it is hidden for this, or empty.</param>
    /// <param name="mayHide">Nothing else is going on with the original: a window of its own, shown, not in use.</param>
    /// <param name="originalHere">Windows' documented answer: is the original on the desktop being shown?</param>
    /// <param name="originalDesktop">The desktop the original is on (documented).</param>
    /// <param name="current">The desktop being shown, read where Explorer keeps it (undocumented).</param>
    /// <param name="awayDesktopExists">Whether the remembered desktop still exists, or null when unknown.</param>
    internal static Step Decide(Guid awayFrom, bool mayHide, bool originalHere, Guid? originalDesktop, Guid? current, bool? awayDesktopExists)
    {
        if (awayFrom != Guid.Empty)
        {
            // Back on its desktop, the desktop was closed, or it can no longer be told: show it rather than strand it.
            if (current is null || current == awayFrom || awayDesktopExists == false) return Step.ShowAgain;
            return Step.Nothing;
        }

        if (!mayHide || originalHere || originalDesktop is null || current is null) return Step.Nothing;
        // The undocumented answer must agree with the documented one, or it isn't trusted.
        return current == originalDesktop ? Step.Nothing : Step.Hide;
    }
}
