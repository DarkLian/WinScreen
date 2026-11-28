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
}
