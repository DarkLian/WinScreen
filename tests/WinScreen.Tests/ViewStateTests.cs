using WinScreen.Pip;
using Xunit;
using static WinScreen.Pip.ViewState;

namespace WinScreen.Tests;

/// <summary>
/// What a view does when its hidden original comes to the front, for every combination of what it knows. The rule
/// (also in ARCHITECTURE.md, INV-20):
///
/// | Original hidden by the view (Keep hidden, while away, or while minimized on the taskbar) | Visit on or just ended | Picked from the taskbar or switcher, where listed | Desktop just switched | Answer |
/// |---|---|---|---|---|
/// | no  | any | any | any | Nothing |
/// | yes | yes | any | any | Nothing |
/// | yes | no  | yes | any | Show |
/// | yes | no  | no  | yes | ViewTakesFocus |
/// | yes | no  | no  | no  | PassFocusOn |
///
/// Hidden unless picked (owner, 18 Nov): every way Windows or an app passes the focus on stays hidden, known or not.
/// </summary>
public sealed class ViewStateTests
{
    public static TheoryData<bool, bool, bool, bool, bool, bool, bool> EveryCombination()
    {
        var all = new TheoryData<bool, bool, bool, bool, bool, bool, bool>();
        for (int bits = 0; bits < 128; bits++)
            all.Add((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0, (bits & 32) != 0, (bits & 64) != 0);
        return all;
    }

    /// <summary>The table above, read row by row.</summary>
    private static Arrival Table(bool hiddenByView, bool visit, bool picked, bool switched) =>
        (hiddenByView, visit, picked, switched) switch
        {
            (false, _, _, _) => Arrival.Nothing,
            (true, true, _, _) => Arrival.Nothing,
            (true, false, true, _) => Arrival.Show,
            (true, false, false, true) => Arrival.ViewTakesFocus,
            (true, false, false, false) => Arrival.PassFocusOn,
        };

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void An_arriving_original_gets_the_answer_in_the_table(bool hidden, bool away, bool visiting, bool justAfterVisit,
        bool picked, bool switched, bool minimized)
    {
        Arrival answer = OriginalArrived(new Facts(hidden, away, visiting, justAfterVisit, picked, switched, minimized));
        Assert.Equal(Table(hidden || away || minimized, visiting || justAfterVisit, picked, switched), answer);
    }

    /// <summary>The cases each found as a bug, by name.</summary>
    [Fact]
    public void The_cases_that_were_bugs_keep_the_original_hidden()
    {
        // 17 Nov: VS Code minimized; 17 and 18 Nov: the view's title bar or menu, the taskbar's hidden-icons panel
        // gone. Windows gave the hidden original the focus, unpicked.
        Assert.Equal(Arrival.PassFocusOn, OriginalArrived(new Facts(Hidden: true, Away: false, Visiting: false,
            JustAfterVisit: false, Picked: false, JustAfterDesktopSwitch: false)));
        // 16 Nov: switching to an empty desktop gave the hidden original, which is on every desktop, the focus.
        Assert.Equal(Arrival.ViewTakesFocus, OriginalArrived(new Facts(Hidden: false, Away: true, Visiting: false,
            JustAfterVisit: false, Picked: false, JustAfterDesktopSwitch: true)));
        // The person chose it from the taskbar: it is shown.
        Assert.Equal(Arrival.Show, OriginalArrived(new Facts(Hidden: true, Away: false, Visiting: false,
            JustAfterVisit: false, Picked: true, JustAfterDesktopSwitch: false)));
    }

    public static TheoryData<int> EveryModeCombination()
    {
        var all = new TheoryData<int>();
        for (int bits = 0; bits < 4096; bits++) all.Add(bits);
        return all;
    }

    /// <summary>
    /// Every combination of the view's twelve modes: none of the forbidden pairs passes as fine, and a combination with
    /// none of them is fine. The forbidden pairs, as ARCHITECTURE.md lists them under INV-20:
    /// used directly with the original shown; a visit without direct use, or while minimized or paused; "use it, then
    /// hide again" with the original hidden or visited, or while minimized; direct use not allowed for the app; a visit
    /// with experimental help while experimental features are off; the original out for a drop during a visit, while
    /// Keep hidden is off or while minimized; hidden while away with Keep hidden on or while the original is in use;
    /// hidden while minimized with Keep hidden on, while away or while the original is in use.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryModeCombination))]
    public void Every_combination_of_modes_is_judged_by_the_forbidden_pairs(int bits)
    {
        Modes m = ModesOf(bits);
        var broken = new List<PipStateProblem>();
        if (m.Interactive && !m.Hidden) broken.Add(PipStateProblem.InteractiveWithOriginalShown);
        if (m.Visiting && !m.Interactive) broken.Add(PipStateProblem.VisitWithoutInteractive);
        if (m.Visiting && (m.Suspended || m.InputPaused)) broken.Add(PipStateProblem.VisitWhilePaused);
        if (m.UsingOriginal && (m.Hidden || m.Visiting)) broken.Add(PipStateProblem.UsingHiddenOriginal);
        if (m.UsingOriginal && m.Suspended) broken.Add(PipStateProblem.UsingOriginalWhileMinimized);
        if (m.Interactive && !m.MayInteract) broken.Add(PipStateProblem.InteractiveNotAllowed);
        if (m.Visiting && !m.ExperimentalSwitch && m.VisitUsesExperimental) broken.Add(PipStateProblem.ExperimentalVisitWithoutSwitch);
        if (m.TakingDrop && m.Visiting) broken.Add(PipStateProblem.DropDuringVisit);
        if (m.TakingDrop && (!m.Hidden || m.Suspended)) broken.Add(PipStateProblem.DropWithoutHiding);
        if (m.Away && (m.Hidden || m.UsingOriginal)) broken.Add(PipStateProblem.AwayWhileHiddenOrInUse);
        if (m.OriginalMinimized && (m.Hidden || m.UsingOriginal || m.Away)) broken.Add(PipStateProblem.MinimizedWhileHiddenOrInUse);

        PipStateProblem found = ProblemOf(m);
        if (broken.Count == 0) Assert.Equal(PipStateProblem.None, found);
        else Assert.Contains(found, broken); // One of them, the first in the order checked.
    }

    private static Modes ModesOf(int bits)
    {
        bool B(int i) => (bits & (1 << i)) != 0;
        return new Modes(Interactive: B(0), Hidden: B(1), Visiting: B(2), Suspended: B(3), InputPaused: B(4),
            UsingOriginal: B(5), MayInteract: B(6), ExperimentalSwitch: B(7), VisitUsesExperimental: B(8), TakingDrop: B(9),
            Away: B(10), OriginalMinimized: B(11));
    }

    /// <summary>
    /// Every allowed combination of modes, every request, Keep hidden before direct use either way: no request leads
    /// to a combination that must never happen (4096 × 16 × 2, the allowed ones only).
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryModeCombination))]
    public void No_request_leads_from_an_allowed_state_to_a_forbidden_one(int bits)
    {
        Modes before = ModesOf(bits);
        if (ProblemOf(before) != PipStateProblem.None) return;
        foreach (Request request in Enum.GetValues<Request>())
            foreach (bool hiddenBefore in new[] { false, true })
                foreach (bool minimizedBefore in new[] { false, true })
                {
                    Modes after = Apply(before, request, hiddenBefore, minimizedBefore);
                    Assert.True(ProblemOf(after) == PipStateProblem.None, $"{before} + {request} → {after}: {ProblemOf(after)}");
                }
    }

    [Fact]
    public void The_requests_do_what_the_menu_and_chip_say()
    {
        var shownView = new Modes(Interactive: false, Hidden: false, Visiting: false, Suspended: false, InputPaused: false,
            UsingOriginal: false, MayInteract: true, ExperimentalSwitch: false, VisitUsesExperimental: false);
        Modes hidden = Apply(shownView, Request.KeepHiddenOn);
        Assert.True(hidden.Hidden);
        Modes used = Apply(hidden, Request.InteractiveOn);
        Assert.True(used is { Interactive: true, Hidden: true });
        Assert.Equal(used, Apply(used, Request.KeepHiddenOff)); // Direct use needs it hidden.
        Modes visiting = Apply(used, Request.VisitStarts);
        Assert.True(visiting.Visiting);
        Assert.False(Apply(visiting, Request.Minimize).Visiting);
        Assert.Equal(shownView, Apply(used, Request.InteractiveOff, hiddenBeforeInteractive: false));
        Assert.True(Apply(used, Request.InteractiveOff, hiddenBeforeInteractive: true) is { Hidden: true, Interactive: false });
        Assert.True(Apply(hidden, Request.UseOriginal) is { UsingOriginal: true, Hidden: false });
        // From a minimized view the chip isn't there; asked anyway, nothing happens (it would be "using it while minimized").
        Modes minimized = Apply(hidden, Request.Minimize);
        Assert.Equal(minimized, Apply(minimized, Request.UseOriginal));
        Assert.Equal(minimized, Apply(minimized, Request.VisitStarts));
        // Not where the app may not be used directly.
        Assert.Equal(hidden with { MayInteract = false }, Apply(hidden with { MayInteract = false }, Request.InteractiveOn));
    }

    /// <summary>
    /// The original out for a drop or a drag, and hidden while the person is on another desktop (10-5): when they may
    /// start, that a visit ends for a drag and none starts during one, and that any choice about the original ends both.
    /// </summary>
    [Fact]
    public void Drops_and_desktops_go_through_the_table_too()
    {
        var shownView = new Modes(Interactive: false, Hidden: false, Visiting: false, Suspended: false, InputPaused: false,
            UsingOriginal: false, MayInteract: true, ExperimentalSwitch: false, VisitUsesExperimental: false);
        Modes hidden = Apply(shownView, Request.KeepHiddenOn);
        // A drop needs the original hidden by the person, the view on the screen and the original not otherwise in use.
        Assert.Equal(shownView, Apply(shownView, Request.DropStarts));
        Modes dropping = Apply(hidden, Request.DropStarts);
        Assert.True(dropping.TakingDrop);
        Assert.Equal(dropping, Apply(dropping, Request.DropStarts));
        Modes minimized = Apply(hidden, Request.Minimize);
        Assert.Equal(minimized, Apply(minimized, Request.DropStarts));
        Assert.Equal(hidden, Apply(dropping, Request.DropEnds));
        // A drag picked up during a visit ends the visit; a visit doesn't start while the original is out.
        Modes visiting = Apply(Apply(hidden, Request.InteractiveOn), Request.VisitStarts);
        Modes dragging = Apply(visiting, Request.DropStarts);
        Assert.True(dragging is { TakingDrop: true, Visiting: false });
        Assert.Equal(dragging, Apply(dragging, Request.VisitStarts));
        // Minimizing and every choice about the original end it.
        foreach (Request choice in new[] { Request.Minimize, Request.KeepHiddenOn, Request.ShowOriginal, Request.InteractiveOff, Request.UseOriginal })
            Assert.False(Apply(dragging, choice).TakingDrop, choice.ToString());
        // Hidden while away: only an original that isn't hidden or in use; Keep hidden or showing it ends it.
        Modes away = Apply(shownView, Request.LeftDesktop);
        Assert.True(away.Away);
        Assert.Equal(hidden, Apply(hidden, Request.LeftDesktop));
        Assert.Equal(dropping, Apply(dropping, Request.LeftDesktop));
        Assert.Equal(shownView, Apply(away, Request.BackToDesktop));
        Assert.True(Apply(away, Request.KeepHiddenOn) is { Hidden: true, Away: false });
        Assert.Equal(away, Apply(away, Request.VisitStarts));
        Assert.Equal(away, Apply(away, Request.DropStarts));
    }

    /// <summary>
    /// An original the person minimized, kept on the taskbar and in Alt+Tab: only a shown original not in use goes
    /// there; picking it shows it, any choice about the original ends it, and it is never Keep hidden.
    /// </summary>
    [Fact]
    public void A_minimized_original_kept_on_the_taskbar_goes_through_the_table_too()
    {
        var shownView = new Modes(Interactive: false, Hidden: false, Visiting: false, Suspended: false, InputPaused: false,
            UsingOriginal: false, MayInteract: true, ExperimentalSwitch: false, VisitUsesExperimental: false);
        Modes minimized = Apply(shownView, Request.OriginalMinimized);
        Assert.True(minimized is { OriginalMinimized: true, Hidden: false });
        Modes hidden = Apply(shownView, Request.KeepHiddenOn);
        Assert.Equal(hidden, Apply(hidden, Request.OriginalMinimized));
        Modes away = Apply(shownView, Request.LeftDesktop);
        Assert.Equal(away, Apply(away, Request.OriginalMinimized));
        Assert.Equal(minimized, Apply(minimized, Request.LeftDesktop));
        Assert.Equal(minimized, Apply(minimized, Request.DropStarts));
        Assert.Equal(shownView, Apply(minimized, Request.OriginalRestored));
        Assert.Equal(shownView, Apply(minimized, Request.ShowOriginal));
        Assert.True(Apply(minimized, Request.KeepHiddenOn) is { Hidden: true, OriginalMinimized: false });
        // Used directly, then Watch only: minimized again, not shown.
        Modes used = Apply(minimized, Request.InteractiveOn);
        Assert.True(used is { Interactive: true, Hidden: true, OriginalMinimized: false });
        Assert.Equal(minimized, Apply(used, Request.InteractiveOff, hiddenBeforeInteractive: false, minimizedBeforeInteractive: true));
        Assert.Equal(shownView, Apply(used, Request.InteractiveOff, hiddenBeforeInteractive: false, minimizedBeforeInteractive: false));
        // Picked from the taskbar or Alt+Tab it is shown; anything else passes the focus on.
        Assert.Equal(Arrival.Show, OriginalArrived(new Facts(Hidden: false, Away: false, Visiting: false,
            JustAfterVisit: false, Picked: true, JustAfterDesktopSwitch: false, Minimized: true)));
        Assert.Equal(Arrival.PassFocusOn, OriginalArrived(new Facts(Hidden: false, Away: false, Visiting: false,
            JustAfterVisit: false, Picked: false, JustAfterDesktopSwitch: false, Minimized: true)));
    }
}
