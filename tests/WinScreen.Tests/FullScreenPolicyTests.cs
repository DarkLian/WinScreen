using WinScreen.Settings;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class FullScreenPolicyTests
{
    private static FullScreenAction Decide(AppCommand command, FullScreenState state, bool pause = true,
        bool allowPip = false, bool targetHasView = false, bool anyViewOpen = false) =>
        FullScreenPolicy.Decide(command, state, pause, allowPip, targetHasView, anyViewOpen);

    [Theory]
    [InlineData(FullScreenState.None)]
    [InlineData(FullScreenState.Busy)]
    [InlineData(FullScreenState.Exclusive)]
    [InlineData(FullScreenState.Presentation)]
    public void A_view_can_always_be_closed_in_every_state(FullScreenState state)
    {
        Assert.Equal(FullScreenAction.Run, Decide(AppCommand.OpenPip, state, targetHasView: true));
        Assert.NotEqual(FullScreenAction.Refuse, Decide(AppCommand.OpenPip, state, anyViewOpen: true));
    }

    [Theory]
    [InlineData(FullScreenState.Busy)]
    [InlineData(FullScreenState.Exclusive)]
    [InlineData(FullScreenState.Presentation)]
    public void The_hotkey_pressed_in_a_game_closes_the_latest_view(FullScreenState state) =>
        Assert.Equal(FullScreenAction.CloseLatestView, Decide(AppCommand.OpenPip, state, anyViewOpen: true));

    [Theory]
    [InlineData(FullScreenState.Busy)]
    [InlineData(FullScreenState.Exclusive)]
    [InlineData(FullScreenState.Presentation)]
    public void Other_commands_stay_paused_by_default(FullScreenState state) =>
        Assert.Equal(FullScreenAction.Refuse, Decide(AppCommand.TogglePin, state));

    /// <summary>Taking click-through, pins, Anchors or the toolbar away never waits for a game to end.</summary>
    [Theory]
    [InlineData(AppCommand.ClickThroughOffEverywhere)]
    [InlineData(AppCommand.UnpinEverywhere)]
    [InlineData(AppCommand.HideAnchors)]
    [InlineData(AppCommand.HideToolbar)]
    public void Commands_for_every_window_always_run(AppCommand command)
    {
        foreach (FullScreenState state in Enum.GetValues<FullScreenState>())
            Assert.Equal(FullScreenAction.Run, Decide(command, state));
        Assert.True(CommandCatalog.ForAllWindows(command));
        Assert.Contains(command, CommandCatalog.All);
        Assert.False(new AppSettings().Hotkeys.ContainsKey(command));
    }

    [Fact]
    public void Exclusive_mode_explains_itself_instead_of_opening_a_view() =>
        Assert.Equal(FullScreenAction.ExplainExclusive, Decide(AppCommand.OpenPip, FullScreenState.Exclusive));

    [Fact]
    public void The_opt_in_only_covers_borderless_full_screen()
    {
        Assert.Equal(FullScreenAction.Run, Decide(AppCommand.OpenPip, FullScreenState.Busy, allowPip: true));
        Assert.Equal(FullScreenAction.ExplainExclusive,
            Decide(AppCommand.OpenPip, FullScreenState.Exclusive, allowPip: true));
        Assert.Equal(FullScreenAction.Refuse, Decide(AppCommand.OpenPip, FullScreenState.Presentation, allowPip: true));
        Assert.Equal(FullScreenAction.Refuse, Decide(AppCommand.TogglePin, FullScreenState.Busy, allowPip: true));
    }

    [Fact]
    public void Choosing_a_source_follows_the_same_opt_in()
    {
        Assert.Equal(FullScreenAction.Refuse, Decide(AppCommand.PickPipSource, FullScreenState.Busy, anyViewOpen: true));
        Assert.Equal(FullScreenAction.Run, Decide(AppCommand.PickPipSource, FullScreenState.Busy, allowPip: true));
        Assert.Equal(FullScreenAction.Refuse, Decide(AppCommand.PickPipSource, FullScreenState.Exclusive, allowPip: true));
    }

    [Fact]
    public void Turning_pause_off_lets_everything_run()
    {
        foreach (FullScreenState state in Enum.GetValues<FullScreenState>())
            Assert.Equal(FullScreenAction.Run, Decide(AppCommand.TogglePin, state, pause: false));
    }

    [Fact]
    public void New_settings_keep_the_safe_default() => Assert.False(new AppSettings().AllowPipInFullScreen);
}
