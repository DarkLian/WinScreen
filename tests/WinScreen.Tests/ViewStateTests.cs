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
}
