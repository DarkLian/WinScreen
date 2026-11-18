namespace WinScreen.Pip;

// Many apps stop drawing while their window's desktop isn't shown, so the view would freeze. While the person is on
// another desktop the original is hidden for now (never the person's own Keep hidden), and shown again on its desktop.
internal static class AwayFromDesktop
{
    internal enum Step { Nothing, Hide, ShowAgain }

    // originalHere and originalDesktop are Windows' documented answers; current is read where Explorer keeps it
    // (undocumented).
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
