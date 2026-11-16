using WinScreen.Pip;
using Xunit;
using static WinScreen.Pip.ViewState;

namespace WinScreen.Tests;

/// <summary>
/// What a view does when its hidden original comes to the front, for every combination of what it knows. The rule
/// (also in ARCHITECTURE.md, INV-20):
///
/// | Original hidden by the view (Keep hidden, or while away) | Visit on or just ended | Windows handed the focus on | Desktop just switched | Answer |
/// |---|---|---|---|---|
/// | no  | any | any | any | Nothing |
/// | yes | yes | any | any | Nothing |
/// | yes | no  | yes | any | PassFocusOn |
/// | yes | no  | no  | yes | ViewTakesFocus |
/// | yes | no  | no  | no  | Show |
/// </summary>
public sealed class ViewStateTests
{
    public static TheoryData<bool, bool, bool, bool, bool, bool> EveryCombination()
    {
        var all = new TheoryData<bool, bool, bool, bool, bool, bool>();
        for (int bits = 0; bits < 64; bits++)
            all.Add((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0, (bits & 32) != 0);
        return all;
    }

    /// <summary>The table above, read row by row.</summary>
    private static Arrival Table(bool hiddenByView, bool visit, bool handedOn, bool switched) =>
        (hiddenByView, visit, handedOn, switched) switch
        {
            (false, _, _, _) => Arrival.Nothing,
            (true, true, _, _) => Arrival.Nothing,
            (true, false, true, _) => Arrival.PassFocusOn,
            (true, false, false, true) => Arrival.ViewTakesFocus,
            (true, false, false, false) => Arrival.Show,
        };

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void An_arriving_original_gets_the_answer_in_the_table(bool hidden, bool away, bool visiting, bool justAfterVisit,
        bool handedOn, bool switched)
    {
        Arrival answer = OriginalArrived(new Facts(hidden, away, visiting, justAfterVisit, handedOn, switched));
        Assert.Equal(Table(hidden || away, visiting || justAfterVisit, handedOn, switched), answer);
    }

    /// <summary>The cases each found as a bug, by name.</summary>
    [Fact]
    public void The_cases_that_were_bugs_keep_the_original_hidden()
    {
        // 17 Nov: VS Code minimized, Windows gave the hidden Chrome the focus.
        Assert.Equal(Arrival.PassFocusOn, OriginalArrived(new Facts(Hidden: true, Away: false, Visiting: false,
            JustAfterVisit: false, HandedOn: true, JustAfterDesktopSwitch: false)));
        // 16 Nov: switching to an empty desktop gave the hidden original, which is on every desktop, the focus.
        Assert.Equal(Arrival.ViewTakesFocus, OriginalArrived(new Facts(Hidden: false, Away: true, Visiting: false,
            JustAfterVisit: false, HandedOn: false, JustAfterDesktopSwitch: true)));
        // The person chose it from the taskbar: it is shown.
        Assert.Equal(Arrival.Show, OriginalArrived(new Facts(Hidden: true, Away: false, Visiting: false,
            JustAfterVisit: false, HandedOn: false, JustAfterDesktopSwitch: false)));
    }

    public static TheoryData<int> EveryModeCombination()
    {
        var all = new TheoryData<int>();
        for (int bits = 0; bits < 512; bits++) all.Add(bits);
        return all;
    }

    /// <summary>
    /// Every combination of the view's nine modes: none of the forbidden pairs passes as fine, and a combination with
    /// none of them is fine. The forbidden pairs, as ARCHITECTURE.md lists them under INV-20:
    /// used directly with the original shown; a visit without direct use, or while minimized or paused; "use it, then
    /// hide again" with the original hidden or visited, or while minimized; direct use not allowed for the app; a visit
    /// with experimental help while experimental features are off.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryModeCombination))]
    public void Every_combination_of_modes_is_judged_by_the_forbidden_pairs(int bits)
    {
        bool B(int i) => (bits & (1 << i)) != 0;
        var m = new Modes(Interactive: B(0), Hidden: B(1), Visiting: B(2), Suspended: B(3), InputPaused: B(4),
            UsingOriginal: B(5), MayInteract: B(6), ExperimentalSwitch: B(7), VisitUsesExperimental: B(8));
        var broken = new List<PipStateProblem>();
        if (m.Interactive && !m.Hidden) broken.Add(PipStateProblem.InteractiveWithOriginalShown);
        if (m.Visiting && !m.Interactive) broken.Add(PipStateProblem.VisitWithoutInteractive);
        if (m.Visiting && (m.Suspended || m.InputPaused)) broken.Add(PipStateProblem.VisitWhilePaused);
        if (m.UsingOriginal && (m.Hidden || m.Visiting)) broken.Add(PipStateProblem.UsingHiddenOriginal);
        if (m.UsingOriginal && m.Suspended) broken.Add(PipStateProblem.UsingOriginalWhileMinimized);
        if (m.Interactive && !m.MayInteract) broken.Add(PipStateProblem.InteractiveNotAllowed);
        if (m.Visiting && !m.ExperimentalSwitch && m.VisitUsesExperimental) broken.Add(PipStateProblem.ExperimentalVisitWithoutSwitch);

        PipStateProblem found = ProblemOf(m);
        if (broken.Count == 0) Assert.Equal(PipStateProblem.None, found);
        else Assert.Contains(found, broken); // One of them, the first in the order checked.
    }
}
