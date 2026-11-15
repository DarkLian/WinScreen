using WinScreen.Pip;
using Xunit;
using Step = WinScreen.Pip.AwayFromDesktop.Step;

namespace WinScreen.Tests;

/// <summary>When a view hides its original for now because the person is on another virtual desktop.</summary>
public sealed class AwayFromDesktopTests
{
    private static readonly Guid Home = new("6d1a8dd0-0000-0000-0000-000000000001");
    private static readonly Guid Other = new("6d1a8dd0-0000-0000-0000-000000000002");

    [Fact]
    public void An_original_on_another_desktop_is_hidden_for_now() =>
        Assert.Equal(Step.Hide, AwayFromDesktop.Decide(Guid.Empty, mayHide: true, originalHere: false, Home, Other, null));

    [Fact]
    public void An_original_on_the_desktop_in_front_is_left_alone() =>
        Assert.Equal(Step.Nothing, AwayFromDesktop.Decide(Guid.Empty, mayHide: true, originalHere: true, Home, Home, null));

    [Fact]
    public void Nothing_is_hidden_when_something_else_is_going_on_with_the_original() =>
        Assert.Equal(Step.Nothing, AwayFromDesktop.Decide(Guid.Empty, mayHide: false, originalHere: false, Home, Other, null));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Nothing_is_hidden_when_a_desktop_can_not_be_told(bool knowOriginal, bool knowCurrent) =>
        Assert.Equal(Step.Nothing, AwayFromDesktop.Decide(Guid.Empty, true, false, knowOriginal ? Home : null, knowCurrent ? Other : null, null));

    [Fact]
    public void The_undocumented_answer_is_not_trusted_when_it_disagrees_with_the_documented_one() =>
        // Windows says the original isn't on the desktop in front, yet the desktop in front reads as its own.
        Assert.Equal(Step.Nothing, AwayFromDesktop.Decide(Guid.Empty, true, originalHere: false, Home, Home, null));

    [Fact]
    public void It_stays_hidden_while_the_person_is_elsewhere() =>
        Assert.Equal(Step.Nothing, AwayFromDesktop.Decide(Home, false, true, null, Other, true));

    [Fact]
    public void It_comes_back_on_its_own_desktop() =>
        Assert.Equal(Step.ShowAgain, AwayFromDesktop.Decide(Home, false, true, null, Home, true));

    [Fact]
    public void It_comes_back_when_its_desktop_was_closed() =>
        Assert.Equal(Step.ShowAgain, AwayFromDesktop.Decide(Home, false, true, null, Other, false));

    [Fact]
    public void It_comes_back_when_the_desktop_in_front_can_no_longer_be_told() =>
        Assert.Equal(Step.ShowAgain, AwayFromDesktop.Decide(Home, false, true, null, null, null));

    [Fact]
    public void An_unreadable_desktop_list_keeps_it_hidden_until_the_person_is_back() =>
        Assert.Equal(Step.Nothing, AwayFromDesktop.Decide(Home, false, true, null, Other, null));
}
