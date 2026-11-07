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
    public override bool IsAvailable => identity.Matches();
    public override bool IsMinimized => User32.IsIconic(identity.Window);
    public override bool ShowsCursor => false;
    public override Screen Screen => Screen.FromHandle(identity.Window);
    public override nint Monitor => PipDisplay.MonitorFromWindow(identity.Window);
    public override int Dpi => Native.Dpi.Of(identity.Window);
    public override int RefreshRate => PipDisplay.CachedRefresh(identity.Window);
    public override Size SurfaceSize => Size.Empty;
    public override Rectangle Whole(Size surface) => WindowInfo.VisiblePart(identity.Window, surface);
    public override Point CaptureOrigin(Rectangle whole) => whole.Location;
    public override GraphicsCaptureItem CreateItem() => WgcInterop.Item(identity.Window);
    public override void Require() => identity.Require();
}
