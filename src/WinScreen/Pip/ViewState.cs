namespace WinScreen.Pip;

/// <summary>
/// What a view does when its hidden original comes to the front, decided in one place from what the view knows
/// (roadmap step 12.2, first part). Each answer here was once a bug: the focus Windows hands on when a window is
/// minimized or closed (17 Nov), or on a desktop switch (16 Nov), was read as the person asking for the window, which
/// showed it and turned Keep hidden off. The table in <c>ViewStateTests</c> is the written rule.
/// </summary>
internal static class ViewState
{
    /// <summary>What to do about the original having come to the front.</summary>
    internal enum Arrival
    {
        /// <summary>Nothing: it isn't hidden by this view, or a visit gave it the focus. (A drop is handled before.)</summary>
        Nothing,
        /// <summary>Windows passed the focus on; it stays hidden and the focus goes where it would have gone.</summary>
        PassFocusOn,
        /// <summary>A desktop switch handed it the focus; it stays hidden and the view takes the focus.</summary>
        ViewTakesFocus,
        /// <summary>Something else brought it forward (its taskbar button, Alt+Tab, the app): it is shown.</summary>
        Show,
    }

    /// <summary>What the view knows at the moment the original came to the front.</summary>
    /// <param name="Hidden">Keep hidden is on.</param>
    /// <param name="Away">Hidden only because the person is on another desktop.</param>
    /// <param name="Visiting">A visit (Interactive) is under way: its own press gives the original the focus.</param>
    /// <param name="JustAfterVisit">A visit ended a moment ago and the focus is still going back.</param>
    /// <param name="HandedOn">The window that had the focus was just minimized or closed, or this view minimized.</param>
    /// <param name="JustAfterDesktopSwitch">The desktop in front changed a moment ago.</param>
    internal readonly record struct Facts(bool Hidden, bool Away, bool Visiting, bool JustAfterVisit,
        bool HandedOn, bool JustAfterDesktopSwitch);

    internal static Arrival OriginalArrived(Facts facts)
    {
        if (!facts.Hidden && !facts.Away) return Arrival.Nothing;
        if (facts.Visiting || facts.JustAfterVisit) return Arrival.Nothing;
        if (facts.HandedOn) return Arrival.PassFocusOn;
        if (facts.JustAfterDesktopSwitch) return Arrival.ViewTakesFocus;
        return Arrival.Show;
    }

    /// <summary>The view's modes at one moment, as the checks below see them.</summary>
    /// <param name="Interactive">Used directly (the menu's tick).</param>
    /// <param name="Hidden">Keep hidden (the menu's tick).</param>
    /// <param name="Visiting">A visit is under way.</param>
    /// <param name="Suspended">The view is minimized.</param>
    /// <param name="InputPaused">Its input is paused (a menu open, the screen blocked).</param>
    /// <param name="UsingOriginal">"Use it, then hide again" is going on.</param>
    /// <param name="MayInteract">Its kind of app, or the experimental switch, allows direct use.</param>
    /// <param name="ExperimentalSwitch">Experimental features are on.</param>
    /// <param name="VisitUsesExperimental">The visit has Enhanced hover or middle-click scrolling.</param>
    internal readonly record struct Modes(bool Interactive, bool Hidden, bool Visiting, bool Suspended, bool InputPaused,
        bool UsingOriginal, bool MayInteract, bool ExperimentalSwitch, bool VisitUsesExperimental);

    /// <summary>The first combination of modes that must never happen, or none (checked on every change of mode).</summary>
    internal static PipStateProblem ProblemOf(Modes m)
    {
        if (m.Interactive && !m.Hidden) return PipStateProblem.InteractiveWithOriginalShown;
        if (m.Visiting && !m.Interactive) return PipStateProblem.VisitWithoutInteractive;
        if (m.Visiting && (m.Suspended || m.InputPaused)) return PipStateProblem.VisitWhilePaused;
        if (m.UsingOriginal && (m.Hidden || m.Visiting)) return PipStateProblem.UsingHiddenOriginal;
        if (m.UsingOriginal && m.Suspended) return PipStateProblem.UsingOriginalWhileMinimized;
        if (m.Interactive && !m.MayInteract) return PipStateProblem.InteractiveNotAllowed;
        if (m.Visiting && !m.ExperimentalSwitch && m.VisitUsesExperimental) return PipStateProblem.ExperimentalVisitWithoutSwitch;
        return PipStateProblem.None;
    }
}
