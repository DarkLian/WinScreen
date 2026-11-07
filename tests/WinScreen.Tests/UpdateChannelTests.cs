using System.Runtime.InteropServices;
using WinScreen.Updates;
using Xunit;

namespace WinScreen.Tests;

public sealed class UpdateChannelTests
{
    [Fact]
    public void Each_architecture_keeps_its_own_feed_and_x64_keeps_the_existing_one()
    {
        Assert.Equal("win", UpdateChannel.For(Architecture.X64));
        Assert.Equal("win-arm64", UpdateChannel.For(Architecture.Arm64));
    }

    [Fact]
    public void This_build_uses_the_feed_for_what_it_is_running_on() =>
        Assert.Equal(UpdateChannel.For(RuntimeInformation.ProcessArchitecture), UpdateChannel.Current);
}
