using static WinScreen.Localization.UiText;
using Windows.Graphics.Capture;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

internal abstract class PipSource
{
    public abstract string Title { get; }

    public abstract nint Window { get; }

    public bool IsWindow => Window != 0;

    public abstract bool IsAvailable { get; }
    public abstract bool IsMinimized { get; }
    public abstract bool ShowsCursor { get; }
    public abstract Screen Screen { get; }
    public abstract nint Monitor { get; }
    public abstract int Dpi { get; }
    public abstract int RefreshRate { get; }

    public abstract Size SurfaceSize { get; }

    public abstract Rectangle Whole(Size surface);

    public abstract Point CaptureOrigin(Rectangle whole);

    public abstract GraphicsCaptureItem CreateItem();

    public abstract void Require();
}

internal sealed class WindowPipSource(WindowIdentity identity) : PipSource
{
    public override string Title => WindowInfo.Title(identity.Window);
    public override nint Window => identity.Window;
    private long? _processCheckedAt;
    // Injectable only inside the assembly, to verify cadence without wall-clock sleeps or process reuse.
    internal Func<long> AvailabilityClock { get; init; } = static () => Environment.TickCount64;
    internal Func<bool> ProcessMatches { get; init; } = identity.Matches;

    // Asked several times every 50 ms. WinScreen's marker on the window goes when the window does, so it tells a closed
    // or reused handle apart on every call; the process check opens the process, so it runs at most once a second while
    // passing.
    // Changing a window still needs the full Require.
    public override bool IsAvailable
    {
        get
        {
            if (identity.Token == 0) return ProcessMatches();
            if (!identity.StillThere()) return false;
            long now = AvailabilityClock();
            if (_processCheckedAt is { } checkedAt && now - checkedAt < 1000) return true;
            if (!ProcessMatches()) return false;
            _processCheckedAt = now;
            return true;
        }
    }
    public override bool IsMinimized => User32.IsIconic(identity.Window);
    public override bool ShowsCursor => false;
    public override Screen Screen => Screen.FromHandle(identity.Window);
    public override nint Monitor => Native.User32.MonitorFromWindow(identity.Window);
    public override int Dpi => Native.Dpi.Of(identity.Window);
    public override int RefreshRate => PipDisplay.CachedRefresh(identity.Window);
    public override Size SurfaceSize => Size.Empty;
    public override Rectangle Whole(Size surface) => WindowInfo.VisiblePart(identity.Window, surface);
    public override Point CaptureOrigin(Rectangle whole) => whole.Location;
    public override GraphicsCaptureItem CreateItem()
    {
        identity.Require();
        return WgcInterop.Item(identity.Window);
    }
    public override void Require() => identity.Require();
}

// Displays are remembered by device name: a monitor handle doesn't survive display changes. A region is in physical
// pixels from the display's corner, on that one display.
internal sealed class ScreenPipSource(string device, Rectangle? region) : PipSource
{
    public string Device => device;
    public Rectangle? Region => region;
    private Screen? Display => Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == device);

    public override string Title => region is null
        ? string.Format(T("pip.source.display"), Number)
        : string.Format(T("pip.source.region"), Number);

    private string Number => new(device.Where(char.IsAsciiDigit).ToArray());

    public override nint Window => 0;
    public override bool IsAvailable => Display is { } display && !Clamped(display.Bounds.Size).IsEmpty;
    public override bool IsMinimized => false;
    public override bool ShowsCursor => true;
    public override Screen Screen => Display ?? Screen.PrimaryScreen!;
    public override nint Monitor => Display is { } display ? PipDisplay.MonitorOf(display) : 0;
    public override int Dpi => Native.Dpi.Default * Native.Dpi.ScaleOf(Monitor) / 100;
    public override int RefreshRate => PipDisplay.CachedRefresh(device);
    public override Size SurfaceSize => Display?.Bounds.Size ?? Size.Empty;
    public override Rectangle Whole(Size surface) => Clamped(surface);
    public override Point CaptureOrigin(Rectangle whole) => Point.Empty;
    public override GraphicsCaptureItem CreateItem()
    {
        Require();
        return WgcInterop.ItemForMonitor(Monitor);
    }

    public override void Require()
    {
        if (!IsAvailable)
            throw new WindowOperationException(T("errors.pip.displayUnavailable"));
    }

    internal Rectangle Clamped(Size display)
    {
        var all = new Rectangle(Point.Empty, display);
        return region is { } part ? Rectangle.Intersect(part, all) : all;
    }
}
