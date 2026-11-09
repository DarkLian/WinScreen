using static WinScreen.Localization.UiText;
using Windows.Graphics.Capture;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>What a view shows. Everything a view needs to know about its source goes through here.</summary>
internal abstract class PipSource
{
    public abstract string Title { get; }

    /// <summary>The source window, or 0 for a part of the screen.</summary>
    public abstract nint Window { get; }

    /// <summary>Only a window can be hidden, gone to, clicked through to, or shown by the compositor.</summary>
    public bool IsWindow => Window != 0;

    public abstract bool IsAvailable { get; }
    public abstract bool IsMinimized { get; }
    public abstract bool ShowsCursor { get; }
    public abstract Screen Screen { get; }
    public abstract nint Monitor { get; }
    public abstract int Dpi { get; }
    public abstract int RefreshRate { get; }

    /// <summary>The size of the source's capture surface, when no compositor view can report it.</summary>
    public abstract Size SurfaceSize { get; }

    /// <summary>The part of the surface a view shows when it isn't cropped.</summary>
    public abstract Rectangle Whole(Size surface);

    /// <summary>Where capture coordinates start, in the coordinates of <see cref="Whole"/>.</summary>
    public abstract Point CaptureOrigin(Rectangle whole);

    public abstract GraphicsCaptureItem CreateItem();

    /// <exception cref="WindowOperationException">The source has gone.</exception>
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

    /// <summary>
    /// Asked several times every 50 ms. The marker WinScreen put on the window goes when the window does, so it tells
    /// a closed or reused handle apart on every call; the process check (it opens the process) runs at most once a
    /// second while it keeps passing. Changing a window still requires the full check (<see cref="Require"/>).
    /// </summary>
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
    public override nint Monitor => PipDisplay.MonitorFromWindow(identity.Window);
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

/// <summary>
/// A whole display, or one rectangle of it. Displays are remembered by device name because a monitor
/// handle doesn't survive display changes. A region is a crop of the display, in physical pixels
/// measured from the display's corner, and it stays on that one display.
/// </summary>
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

    /// <summary>What's left of the region on a display of this size; a region off the display is empty.</summary>
    internal Rectangle Clamped(Size display)
    {
        var all = new Rectangle(Point.Empty, display);
        return region is { } part ? Rectangle.Intersect(part, all) : all;
    }
}
