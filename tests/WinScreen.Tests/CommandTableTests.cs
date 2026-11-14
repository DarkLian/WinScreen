using System.Runtime.CompilerServices;
using WinScreen.Settings;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Every command is defined once, and Put back's orders are written once (roadmap P1-6).</summary>
public sealed class CommandTableTests
{
    /// <summary>The definitions as the running app builds them. Building them touches no state, so no app is started.</summary>
    private static CommandDefinition[] Definitions() =>
        ((TrayApplication)RuntimeHelpers.GetUninitializedObject(typeof(TrayApplication))).CommandAreas().SelectMany(area => area).ToArray();

    [Fact]
    public void Every_command_has_exactly_one_definition()
    {
        CommandDefinition[] definitions = Definitions();
        Assert.Empty(definitions.GroupBy(definition => definition.Command).Where(group => group.Count() > 1).Select(group => group.Key));
        Assert.Empty(Enum.GetValues<AppCommand>().Except(definitions.Select(definition => definition.Command)));
        _ = new CommandTable(definitions); // and the table accepts them
    }

    [Fact]
    public void A_command_defined_twice_is_refused()
    {
        var twice = new CommandDefinition(AppCommand.TogglePin, (_, _) => { });
        Assert.Throws<InvalidOperationException>(() => new CommandTable([twice], [twice]));
    }

    [Fact]
    public void What_a_command_works_on_matches_the_catalog()
    {
        foreach (CommandDefinition definition in Definitions())
        {
            bool forAll = definition.Target is CommandTarget.NoWindow or CommandTarget.EveryWindow;
            Assert.True(forAll == CommandCatalog.ForAllWindows(definition.Command), $"{definition.Command} is {definition.Target}.");
            Assert.Equal(CommandCatalog.Everywhere.Contains(definition.Command), definition.Target == CommandTarget.EveryWindow);
        }
        Assert.Equal([AppCommand.RestoreWindow, AppCommand.RestoreAll], Definitions().Where(d => d.Recovers).Select(d => d.Command));
    }

    [Fact]
    public void Put_back_and_exit_take_every_part_away_once_in_the_order_they_always_have()
    {
        // The orders as they were written out by hand before P1-6 step (c): Put back closes WinScreen's own
        // surfaces first; exit restores the windows first and stops there if that fails.
        Assert.Equal(
            [WindowPart.CropChoice, WindowPart.View, WindowPart.Sound, WindowPart.Tile, WindowPart.FilterDark, WindowPart.ContentEffect, WindowPart.Anchor, WindowPart.WindowChanges],
            PutBackOrder.PutBack);
        Assert.Equal(
            [WindowPart.CropChoice, WindowPart.WindowChanges, WindowPart.View, WindowPart.Sound, WindowPart.Tile, WindowPart.FilterDark, WindowPart.ContentEffect, WindowPart.Anchor],
            PutBackOrder.Exit);
        foreach (WindowPart[] order in new[] { PutBackOrder.PutBack, PutBackOrder.Exit })
            Assert.Equal(Enum.GetValues<WindowPart>().Order(), order.Order());
    }

    /// <summary>
    /// Each way of looking at a part, or taking it away, has a case for every part: a part without one would be
    /// skipped by Put back without a word. (They don't throw instead, since a throw would stop Put back halfway.)
    /// </summary>
    [Theory]
    [InlineData("bool Has(WindowPart part, nint window)")]
    [InlineData("bool TakeAway(WindowPart part, nint window)")]
    [InlineData("int TakeAwayEverywhere(WindowPart part)")]
    public void Every_part_has_a_case_wherever_put_back_looks_at_parts(string signature)
    {
        string body = InvariantTests.MethodBody(InvariantTests.WithoutComments(InvariantTests.Source("Tray/TrayApplication.Commands.cs")), signature);
        string[] missing = Enum.GetNames<WindowPart>().Where(part => !body.Contains("WindowPart." + part, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, "No case for: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_part_put_back_takes_away_is_one_a_command_can_change()
    {
        Assert.Equal(Enum.GetValues<WindowPart>().Order(),
            Definitions().SelectMany(definition => definition.Undoes).Distinct().Order());
    }
}
