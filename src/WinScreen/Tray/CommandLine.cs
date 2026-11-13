using System.Runtime.InteropServices;
using static WinScreen.Localization.UiText;
using WinScreen.Hotkeys;
using WinScreen.Native;
using WinScreen.Settings;

namespace WinScreen.Tray;

/// <summary>What a script asked of WinScreen on its command line (roadmap P2-2).</summary>
internal abstract record CommandLineRequest
{
    /// <summary>No command-line request: WinScreen starts as usual.</summary>
    public sealed record None : CommandLineRequest;
    public sealed record ListCommands : CommandLineRequest;
    /// <summary>Run a command in the running copy. Target is "active", "pointer" or "title:text".</summary>
    public sealed record Run(AppCommand Command, string Target) : CommandLineRequest;
    public sealed record Wrong(string Problem) : CommandLineRequest;
}

/// <summary>How a command-line run ended, as the process's exit code.</summary>
internal enum CommandLineExit
{
    Done = 0,
    /// <summary>The command line itself was wrong: an unknown command or option.</summary>
    Usage = 2,
    /// <summary>No WinScreen of this copy is running, or it couldn't be reached.</summary>
    NotRunning = 3,
    /// <summary>WinScreen refused or paused the command: full screen, an administrator app, experimental features off.</summary>
    NotDone = 4,
    /// <summary>The command ran and failed.</summary>
    Failed = 5,
    /// <summary>No window was there to act on.</summary>
    NoWindow = 6,
}

/// <summary>
/// <c>WinScreen.exe --run &lt;command&gt; [--window active|pointer|title:&lt;text&gt;]</c> and
/// <c>WinScreen.exe --list-commands</c>. The command goes to the running copy, which runs it as a hotkey would and
/// answers how it went; nothing is run by this second copy itself.
/// </summary>
internal static class CommandLine
{
    /// <summary>What the WM_COPYDATA from a command line holds: "WSR1".</summary>
    internal const nint MessageKind = 0x57535231;
    private const uint TimeoutMilliseconds = 15000;
    private const uint AbortIfHung = 0x0002;

    /// <summary>Reads the arguments; anything that isn't a command-line request is left to the normal start.</summary>
    internal static CommandLineRequest Parse(IReadOnlyList<string> arguments)
    {
        int list = IndexOf(arguments, "--list-commands"), run = IndexOf(arguments, "--run");
        if (list < 0 && run < 0) return new CommandLineRequest.None();
        if (list >= 0 && run >= 0) return new CommandLineRequest.Wrong(T("cli.oneAtATime"));
        if (list >= 0) return arguments.Count == 1 ? new CommandLineRequest.ListCommands() : new CommandLineRequest.Wrong(T("cli.usage"));

        string target = "active";
        AppCommand? command = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (Is(argument, "--run") && i + 1 < arguments.Count && command is null)
            {
                string name = arguments[++i];
                if (!Enum.GetNames<AppCommand>().Contains(name, StringComparer.OrdinalIgnoreCase))
                    return new CommandLineRequest.Wrong(string.Format(T("cli.unknownCommand"), name));
                command = Enum.Parse<AppCommand>(name, ignoreCase: true);
            }
            else if (Is(argument, "--window") && i + 1 < arguments.Count)
            {
                target = arguments[++i];
                if (!Is(target, "active") && !Is(target, "pointer") &&
                    !(target.StartsWith("title:", StringComparison.OrdinalIgnoreCase) && target.Length > "title:".Length))
                    return new CommandLineRequest.Wrong(T("cli.usage"));
            }
            else
                return new CommandLineRequest.Wrong(T("cli.usage"));
        }
        return command is { } chosen ? new CommandLineRequest.Run(chosen, target) : new CommandLineRequest.Wrong(T("cli.usage"));
    }

    private static bool Is(string argument, string option) => string.Equals(argument, option, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(IReadOnlyList<string> arguments, string option)
    {
        for (int i = 0; i < arguments.Count; i++)
            if (Is(arguments[i], option)) return i;
        return -1;
    }

    /// <summary>What a command's outcome means to a script.</summary>
    internal static CommandLineExit ExitFor(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Started => CommandLineExit.Done,
        CommandOutcome.Failed => CommandLineExit.Failed,
        _ => CommandLineExit.NotDone,
    };

    /// <summary>The text that goes to the running copy, and back from it.</summary>
    internal static string Encode(AppCommand command, string target) => command + "\n" + target;

    internal static (AppCommand Command, string Target)? Decode(string text)
    {
        string[] parts = text.Split('\n', 2);
        return parts.Length == 2 && Enum.TryParse(parts[0], out AppCommand command) && Enum.IsDefined(command)
            ? (command, parts[1])
            : null;
    }

    /// <summary>Handles a command-line request and says what the exit code is. Writes only for --list-commands and problems.</summary>
    internal static CommandLineExit Handle(CommandLineRequest request)
    {
        ParentConsole.Attach();
        switch (request)
        {
            case CommandLineRequest.ListCommands:
                foreach (AppCommand command in Enum.GetValues<AppCommand>())
                    Console.WriteLine($"{command}\t{CommandCatalog.Name(command)}");
                return CommandLineExit.Done;
            case CommandLineRequest.Wrong wrong:
                Console.Error.WriteLine(wrong.Problem);
                return CommandLineExit.Usage;
            case CommandLineRequest.Run run:
                CommandLineExit exit = Send(run.Command, run.Target);
                if (exit != CommandLineExit.Done) Console.Error.WriteLine(MessageOf(exit));
                return exit;
            default:
                return CommandLineExit.Usage;
        }
    }

    private static string MessageOf(CommandLineExit exit) => exit switch
    {
        CommandLineExit.NotRunning => T("cli.notRunning"),
        CommandLineExit.NoWindow => T("cli.noWindow"),
        CommandLineExit.Failed => T("cli.failed"),
        _ => T("cli.notDone"),
    };

    /// <summary>Gives the command to the running copy of this same program, and waits for its answer.</summary>
    private static CommandLineExit Send(AppCommand command, string target)
    {
        nint window = User32.FindWindow(null, HotkeyHost.WindowTitle);
        // Another copy, such as an installed one beside this portable one, isn't the one asked for.
        if (window == 0 || !string.Equals(Windows.WindowInfo.ExecutablePath(window), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return CommandLineExit.NotRunning;
        string text = Encode(command, target);
        nint data = Marshal.StringToHGlobalUni(text);
        nint message = Marshal.AllocHGlobal(Marshal.SizeOf<CopyData>());
        try
        {
            Marshal.StructureToPtr(new CopyData { Kind = MessageKind, Size = (text.Length + 1) * 2, Data = data }, message, false);
            // A copy running as administrator doesn't take messages from one that isn't; that fails here, by design.
            if (User32.SendMessageTimeout(window, User32.WM_COPYDATA, 0, message, AbortIfHung, TimeoutMilliseconds, out nint answer) == 0)
                return CommandLineExit.NotRunning;
            return Enum.IsDefined((CommandLineExit)(int)answer) ? (CommandLineExit)(int)answer : CommandLineExit.Failed;
        }
        finally
        {
            Marshal.FreeHGlobal(message);
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Reads a command-line request a running copy received, or null for anything else.</summary>
    internal static (AppCommand Command, string Target)? Received(nint copyData)
    {
        var data = Marshal.PtrToStructure<CopyData>(copyData);
        if (data.Kind != MessageKind || data.Size <= 0 || data.Size > 4096 || data.Data == 0) return null;
        return Decode(Marshal.PtrToStringUni(data.Data, data.Size / 2).TrimEnd('\0'));
    }
}
