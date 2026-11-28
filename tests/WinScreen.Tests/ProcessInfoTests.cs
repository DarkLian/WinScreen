using System.Diagnostics;
using WinScreen.Native;
using Xunit;

namespace WinScreen.Tests;

public sealed class ProcessInfoTests
{
    // Window identities in recovery records written by older copies were made with Process.StartTime.
    [Fact]
    public void A_start_time_is_the_one_Process_gives()
    {
        using Process self = Process.GetCurrentProcess();
        Assert.Equal(self.StartTime.ToUniversalTime().Ticks, ProcessInfo.StartTicks((uint)Environment.ProcessId));
    }

    [Fact]
    public void A_process_that_is_not_running_has_no_start_time() => Assert.Null(ProcessInfo.StartTicks(uint.MaxValue - 2));

    // Exclusions, rules and profiles are matched by this name; it is read from the path now, not from Process.
    [Fact]
    public void A_program_name_is_the_one_Process_gives()
    {
        foreach (Process process in Process.GetProcesses())
            using (process)
            {
                string expected;
                try { expected = process.ProcessName; }
                catch (InvalidOperationException) { continue; } // Ended meanwhile.
                if (process.Id is 0 or 4 || ProcessInfo.PathOf((uint)process.Id).Length == 0) continue;
                string actual = Windows.WindowInfo.ProcessName((uint)process.Id);
                if (actual.Length == 0) continue; // Ended meanwhile.
                Assert.Equal(expected, actual, StringComparer.OrdinalIgnoreCase);
            }
        Assert.Equal("System", Windows.WindowInfo.ProcessName(4)); // No path to read: Process still answers.
    }
}
