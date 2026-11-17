namespace WinScreen.Pip;

/// <summary>
/// What a view does when its hidden original comes to the front, decided in one place from what the view knows
/// (roadmap step 12.2, first part). It stays hidden unless the person picked it: from its taskbar button or
/// thumbnail, or in the switcher, where it is still listed. Until 18 Nov it was the other way round, shown unless the
/// view recognised Windows passing the focus on, and every way of passing it on the view didn't know (a window
/// minimized or closed, a desktop switch, the view's own title bar or menu, the taskbar's hidden-icons panel) showed
/// it and turned Keep hidden off (owner, 16 to 18 Nov). The table in <c>ViewStateTests</c> is the written rule.
/// </summary>
internal static class ViewState
{
    /// <summary>What to do about the original having come to the front.</summary>
    internal enum Arrival
    {
        /// <summary>Nothing: it isn't hidden by this view, or a visit gave it the focus. (A drop is handled before.)</summary>
        Nothing,
        /// <summary>Not asked for: it stays hidden and the focus goes where it would have gone without it.</summary>
        PassFocusOn,
        /// <summary>A desktop switch handed it the focus; it stays hidden and the view takes the focus.</summary>
        ViewTakesFocus,
        /// <summary>The person picked it from the taskbar or the switcher: it is shown.</summary>
        Show,
    }

    /// <summary>What the view knows at the moment the original came to the front.</summary>
    /// <param name="Hidden">Keep hidden is on.</param>
    /// <param name="Away">Hidden only because the person is on another desktop.</param>
    /// <param name="Visiting">A visit (Interactive) is under way: its own press gives the original the focus.</param>
    /// <param name="JustAfterVisit">A visit ended a moment ago and the focus is still going back.</param>
    /// <param name="Picked">
    /// The person picked it: from the taskbar (a button or thumbnail under the pointer) or the switcher (it had the
    /// focus before), and the original is still listed there.
    /// </param>
    /// <param name="JustAfterDesktopSwitch">The desktop in front changed a moment ago.</param>
    internal readonly record struct Facts(bool Hidden, bool Away, bool Visiting, bool JustAfterVisit,
        bool Picked, bool JustAfterDesktopSwitch);

    internal static Arrival OriginalArrived(Facts facts)
    {
        if (!facts.Hidden && !facts.Away) return Arrival.Nothing;
        if (facts.Visiting || facts.JustAfterVisit) return Arrival.Nothing;
        if (facts.Picked) return Arrival.Show;
        if (facts.JustAfterDesktopSwitch) return Arrival.ViewTakesFocus;
        return Arrival.PassFocusOn;
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

    /// <summary>What someone or something asks of a view.</summary>
    internal enum Request
    {
        /// <summary>Keep hidden ticked.</summary>
        KeepHiddenOn,
        /// <summary>Keep hidden unticked (refused while used directly: that needs it hidden).</summary>
        KeepHiddenOff,
        /// <summary>Used directly, ticked (refused where the app may not be).</summary>
        InteractiveOn,
        /// <summary>Used directly, unticked: Keep hidden goes back to what it was before.</summary>
        InteractiveOff,
        /// <summary>The chip's "use it, then hide again": only for a hidden original, from a view on the screen.</summary>
        UseOriginal,
        /// <summary>Go to it, or the original brought forward by the person: shown, nothing else kept.</summary>
        ShowOriginal,
        /// <summary>The view minimized.</summary>
        Minimize,
        /// <summary>The view back from minimized.</summary>
        Restore,
        /// <summary>A press on the view starts a visit (refused unless used directly, on the screen and not paused).</summary>
        VisitStarts,
        /// <summary>The visit ends.</summary>
        VisitEnds,
    }

    /// <summary>
    /// The modes a view is in after a request, from the modes before (roadmap 12.2, third part). <c>PipWindow</c> does
    /// the work and then checks that it came out here (<c>view.unexpectedMode</c> in the log when not, unless the work
    /// failed and left everything as it was). A request that is refused leaves the modes unchanged.
    /// </summary>
    /// <param name="hiddenBeforeInteractive">Keep hidden as it was when direct use was turned on.</param>
    internal static Modes Apply(Modes m, Request request, bool hiddenBeforeInteractive = false) => request switch
    {
        Request.KeepHiddenOn => m with { Hidden = true, UsingOriginal = false },
        Request.KeepHiddenOff when m.Interactive => m,
        Request.KeepHiddenOff => m with { Hidden = false, Visiting = false, UsingOriginal = false },
        Request.InteractiveOn when m.Interactive || !m.MayInteract => m,
        Request.InteractiveOn => m with { Interactive = true, Hidden = true, UsingOriginal = false },
        Request.InteractiveOff when !m.Interactive => m,
        Request.InteractiveOff => m with { Interactive = false, Hidden = hiddenBeforeInteractive, Visiting = false, UsingOriginal = false },
        Request.UseOriginal when !m.Hidden || m.UsingOriginal || m.Suspended => m,
        Request.UseOriginal => m with { Interactive = false, Hidden = false, Visiting = false, UsingOriginal = true },
        Request.ShowOriginal => m with { Interactive = false, Hidden = false, Visiting = false, UsingOriginal = false },
        Request.Minimize when m.Suspended => m,
        Request.Minimize => m with { Suspended = true, Visiting = false, UsingOriginal = false },
        Request.Restore => m with { Suspended = false },
        Request.VisitStarts when !m.Interactive || !m.Hidden || m.Suspended || m.InputPaused || m.Visiting ||
                                 m.VisitUsesExperimental && !m.ExperimentalSwitch => m,
        Request.VisitStarts => m with { Visiting = true },
        Request.VisitEnds => m with { Visiting = false },
        _ => m,
    };
}
