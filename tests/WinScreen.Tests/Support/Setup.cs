using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WinScreen.Tests.Support;

internal static class Setup
{
    /// <summary>Screen coordinates must be real pixels, as they are in WinScreen itself.</summary>
    [ModuleInitializer]
    internal static void UseRealPixels() => Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
}
