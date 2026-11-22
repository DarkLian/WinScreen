namespace WinScreen.Pip;

// A hidden original that comes to the front stays hidden unless the person picked it (taskbar button or thumbnail, or
// the switcher where it is still listed): every way Windows passes the focus on must not show it and turn Keep hidden
// off. The table in ViewStateTests is the written rule.
internal static class ViewState
{
    internal enum Arrival
    {
        Nothing,
        PassFocusOn,
        ViewTakesFocus,
        Show,
    }

    internal readonly record struct Facts(bool Hidden, bool Away, bool Visiting, bool JustAfterVisit,
        bool Picked, bool JustAfterDesktopSwitch, bool Minimized = false);

    internal static Arrival OriginalArrived(Facts facts)
    {
        if (!facts.Hidden && !facts.Away && !facts.Minimized) return Arrival.Nothing;
        if (facts.Visiting || facts.JustAfterVisit) return Arrival.Nothing;
        if (facts.Picked) return Arrival.Show;
        if (facts.JustAfterDesktopSwitch) return Arrival.ViewTakesFocus;
        return Arrival.PassFocusOn;
    }

    internal readonly record struct Modes(bool Interactive, bool Hidden, bool Visiting, bool Suspended, bool InputPaused,
        bool UsingOriginal, bool MayInteract, bool ExperimentalSwitch, bool VisitUsesExperimental, bool TakingDrop = false,
        bool Away = false, bool OriginalMinimized = false);

    internal static PipStateProblem ProblemOf(Modes m)
    {
        if (m.Interactive && !m.Hidden) return PipStateProblem.InteractiveWithOriginalShown;
        if (m.Visiting && !m.Interactive) return PipStateProblem.VisitWithoutInteractive;
        if (m.Visiting && (m.Suspended || m.InputPaused)) return PipStateProblem.VisitWhilePaused;
        if (m.UsingOriginal && (m.Hidden || m.Visiting)) return PipStateProblem.UsingHiddenOriginal;
        if (m.UsingOriginal && m.Suspended) return PipStateProblem.UsingOriginalWhileMinimized;
        if (m.Interactive && !m.MayInteract) return PipStateProblem.InteractiveNotAllowed;
        if (m.Visiting && !m.ExperimentalSwitch && m.VisitUsesExperimental) return PipStateProblem.ExperimentalVisitWithoutSwitch;
        if (m.TakingDrop && m.Visiting) return PipStateProblem.DropDuringVisit;
        if (m.TakingDrop && (!m.Hidden || m.Suspended)) return PipStateProblem.DropWithoutHiding;
        if (m.Away && (m.Hidden || m.UsingOriginal)) return PipStateProblem.AwayWhileHiddenOrInUse;
        if (m.OriginalMinimized && (m.Hidden || m.UsingOriginal || m.Away)) return PipStateProblem.MinimizedWhileHiddenOrInUse;
        return PipStateProblem.None;
    }

    internal enum Request
    {
        KeepHiddenOn,
        KeepHiddenOff,
        InteractiveOn,
        InteractiveOff,
        UseOriginal,
        ShowOriginal,
        Minimize,
        Restore,
        VisitStarts,
        VisitEnds,
        DropStarts,
        DropEnds,
        LeftDesktop,
        BackToDesktop,
        OriginalMinimized,
        OriginalRestored,
    }

    // Every choice about the original (Keep hidden, used directly, use it, show it) ends a drop, "hidden while away"
    // and "hidden while minimized"; minimizing the view ends a drop. Watch only after direct use goes back to how the
    // original was before: Keep hidden, shown, or minimized.
    internal static Modes Apply(Modes m, Request request, bool hiddenBeforeInteractive = false, bool minimizedBeforeInteractive = false) => request switch
    {
        Request.KeepHiddenOn => m with { Hidden = true, UsingOriginal = false, TakingDrop = false, Away = false, OriginalMinimized = false },
        Request.KeepHiddenOff when m.Interactive => m,
        Request.KeepHiddenOff => m with { Hidden = false, Visiting = false, UsingOriginal = false, TakingDrop = false, Away = false, OriginalMinimized = false },
        Request.InteractiveOn when m.Interactive || !m.MayInteract => m,
        Request.InteractiveOn => m with { Interactive = true, Hidden = true, UsingOriginal = false, TakingDrop = false, Away = false, OriginalMinimized = false },
        Request.InteractiveOff when !m.Interactive => m,
        Request.InteractiveOff => m with { Interactive = false, Hidden = hiddenBeforeInteractive, Visiting = false, UsingOriginal = false, TakingDrop = false, Away = false,
            OriginalMinimized = minimizedBeforeInteractive && !hiddenBeforeInteractive },
        Request.UseOriginal when !m.Hidden || m.UsingOriginal || m.Suspended => m,
        Request.UseOriginal => m with { Interactive = false, Hidden = false, Visiting = false, UsingOriginal = true, TakingDrop = false, Away = false, OriginalMinimized = false },
        Request.ShowOriginal => m with { Interactive = false, Hidden = false, Visiting = false, UsingOriginal = false, TakingDrop = false, Away = false, OriginalMinimized = false },
        Request.Minimize when m.Suspended => m,
        Request.Minimize => m with { Suspended = true, Visiting = false, UsingOriginal = false, TakingDrop = false },
        Request.Restore => m with { Suspended = false },
        Request.VisitStarts when !m.Interactive || !m.Hidden || m.Suspended || m.InputPaused || m.Visiting || m.TakingDrop ||
                                 m.VisitUsesExperimental && !m.ExperimentalSwitch => m,
        Request.VisitStarts => m with { Visiting = true },
        Request.VisitEnds => m with { Visiting = false },
        Request.DropStarts when !m.Hidden || m.Suspended || m.UsingOriginal || m.TakingDrop || m.Away => m,
        Request.DropStarts => m with { TakingDrop = true, Visiting = false },
        Request.DropEnds => m with { TakingDrop = false },
        Request.LeftDesktop when m.Hidden || m.UsingOriginal || m.Visiting || m.TakingDrop || m.Away || m.OriginalMinimized => m,
        Request.LeftDesktop => m with { Away = true },
        Request.BackToDesktop => m with { Away = false },
        Request.OriginalMinimized when m.Hidden || m.UsingOriginal || m.Visiting || m.TakingDrop || m.Away || m.OriginalMinimized => m,
        Request.OriginalMinimized => m with { OriginalMinimized = true },
        Request.OriginalRestored => m with { OriginalMinimized = false },
        _ => m,
    };
}
