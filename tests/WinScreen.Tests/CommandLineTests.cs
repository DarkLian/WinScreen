using WinScreen.Settings;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Reading WinScreen's command line (roadmap P2-2). The running copy's side is in AppEndToEndTests.</summary>
public sealed class CommandLineTests
{
    [Fact]
    public void Without_command_line_options_WinScreen_starts_as_usual()
    {
        Assert.IsType<CommandLineRequest.None>(CommandLine.Parse([]));
        Assert.IsType<CommandLineRequest.None>(CommandLine.Parse(["--elevated"]));
    }

    [Theory]
    [InlineData("--run TogglePin", AppCommand.TogglePin, "active")]
    [InlineData("--RUN togglepin", AppCommand.TogglePin, "active")]
    [InlineData("--run OpenPip --window pointer", AppCommand.OpenPip, "pointer")]
    [InlineData("--window title:Chat --run RestoreWindow", AppCommand.RestoreWindow, "title:Chat")]
    public void A_command_and_its_window_are_read(string line, AppCommand command, string target) =>
        Assert.Equal(new CommandLineRequest.Run(command, target), CommandLine.Parse(line.Split(' ')));

    [Theory]
    [InlineData("--run")]
    [InlineData("--run Nope")]
    [InlineData("--run 3")]
    [InlineData("--run TogglePin --window")]
    [InlineData("--run TogglePin --window title:")]
    [InlineData("--run TogglePin --window somewhere")]
    [InlineData("--run TogglePin --extra")]
    [InlineData("--run TogglePin --list-commands")]
    [InlineData("--list-commands --extra")]
    public void A_wrong_command_line_is_refused_with_a_reason(string line) =>
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<CommandLineRequest.Wrong>(CommandLine.Parse(line.Split(' '))).Problem));

    [Fact]
    public void Listing_the_commands_is_asked_alone() =>
        Assert.IsType<CommandLineRequest.ListCommands>(CommandLine.Parse(["--list-commands"]));

    [Fact]
    public void A_request_reaches_the_running_copy_as_sent()
    {
        foreach (AppCommand command in Enum.GetValues<AppCommand>())
            Assert.Equal((command, "title:a\nb"), CommandLine.Decode(CommandLine.Encode(command, "title:a\nb")));
        Assert.Null(CommandLine.Decode("Nope\nactive"));
        Assert.Null(CommandLine.Decode("99\nactive"));
        Assert.Null(CommandLine.Decode("TogglePin"));
    }

    [Fact]
    public void Each_outcome_has_its_exit_code()
    {
        Assert.Equal(CommandLineExit.Done, CommandLine.ExitFor(CommandOutcome.Started));
        Assert.Equal(CommandLineExit.Failed, CommandLine.ExitFor(CommandOutcome.Failed));
        foreach (CommandOutcome outcome in Enum.GetValues<CommandOutcome>().Except([CommandOutcome.Started, CommandOutcome.Failed]))
            Assert.Equal(CommandLineExit.NotDone, CommandLine.ExitFor(outcome));
    }
}
