using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Glass;

// Kept areas marked on a control follow it, as a video on a page that scrolls: the app says where the control is now.
// Every UI Automation call is made on this one thread, which owns the elements it holds.
internal sealed class KeptAreaFollower : IDisposable
{
    internal const float SameControl = 0.85f;
    private const float FoundAgain = 0.5f;
    private const int Soonest = 8, Check = 500, LookAgain = 1000, StillAfter = 300;
    private const int WindowType = 50032, DocumentType = 50030, PaneType = 50033;

    private readonly nint _window;
    private readonly Action _moved;
    private readonly object _gate = new();
    private readonly AutoResetEvent _poke = new(false);
    private bool _stopping;
    private KeptArea[] _areas = [];
    private long _asked, _answered;
    private volatile Placed _placed = new([], []);
    private volatile bool _holding;
    private long _movedAt = long.MinValue / 2;

    private sealed record Placed(KeptArea[] For, Rectangle?[] Places);

    // moved is raised on the follower's thread whenever a place changes.
    public KeptAreaFollower(nint window, Action moved)
    {
        _window = window;
        _moved = moved;
        var thread = new Thread(Run) { IsBackground = true, Name = "WinScreen kept areas" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public bool Holding => _holding;

    // A control moved a moment ago, as while a page scrolls: a new frame is then worth waiting for its place. A video
    // playing in a still page isn't.
    public bool Moving => Environment.TickCount64 - Interlocked.Read(ref _movedAt) < StillAfter;

    public void SetAreas(KeptArea[] areas)
    {
        lock (_gate)
        {
            if (_stopping) return;
            _areas = areas;
            _asked++;
            _poke.Set();
        }
    }

    // Something changed in the window, such as a new frame: where are the controls now.
    public long Ask()
    {
        lock (_gate)
        {
            if (_stopping) return 0;
            _poke.Set();
            return ++_asked;
        }
    }

    public void WaitFor(long asked, int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        lock (_gate)
        {
            while (!_stopping && _answered < asked)
            {
                long left = end - Environment.TickCount64;
                if (left <= 0) return;
                Monitor.Wait(_gate, (int)left);
            }
        }
    }

    // In the window's frame; Empty for a control out of sight or not there, null for an area drawn where it was marked.
    public Rectangle?[]? PlacesFor(KeptArea[] kept) => _placed is var placed && ReferenceEquals(placed.For, kept) ? placed.Places : null;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _poke.Set();
            Monitor.PulseAll(_gate);
        }
    }

    // The area with the control it was marked over, or as it was when the marked part isn't one.
    internal static KeptArea Described(KeptArea area, nint window, Rectangle marked)
    {
        nint automation = ElementFinder.CreateAutomation(), walker = 0;
        List<nint>? chain = null;
        try
        {
            if (automation == 0 || (walker = ElementFinder.ControlWalkerOf(automation)) == 0) return area;
            chain = ElementFinder.ChainAt(automation, walker, window, Middle(marked));
            int best = chain is null ? -1 : Best(chain, marked, SameControl, null);
            if (best < 0) return area;
            nint element = chain![best];
            return (area with
            {
                ControlType = ElementFinder.ControlTypeOf(element), ControlClass = ElementFinder.ClassNameOf(element),
                ControlId = ElementFinder.AutomationIdOf(element),
            }).Validated();
        }
        catch (Exception failure) when (failure is COMException or InvalidCastException)
        {
            Diagnostics.Log.Swallowed(failure);
            return area;
        }
        finally
        {
            foreach (nint element in chain ?? []) Marshal.Release(element);
            if (walker != 0) Marshal.Release(walker);
            if (automation != 0) Marshal.Release(automation);
        }
    }

    private void Run()
    {
        nint automation = 0, walker = 0;
        Tracked[] tracked = [];
        KeptArea[]? current = null;
        try
        {
            automation = ElementFinder.CreateAutomation();
            walker = automation == 0 ? 0 : ElementFinder.ControlWalkerOf(automation);
            while (true)
            {
                int wait = tracked.Any(t => t.Element != 0) ? Check : tracked.Any(t => t.Area.Follows) ? LookAgain : Timeout.Infinite;
                _poke.WaitOne(wait);
                long asked;
                KeptArea[] areas;
                lock (_gate)
                {
                    if (_stopping) break;
                    asked = _asked;
                    areas = _areas;
                }
                if (!ReferenceEquals(areas, current))
                {
                    foreach (Tracked t in tracked) t.Release();
                    tracked = [.. areas.Select(area => new Tracked(area))];
                    current = areas;
                }

                var places = new Rectangle?[areas.Length];
                if (walker != 0)
                {
                    try { Round(automation, walker, tracked, asked, places); }
                    catch (Exception failure) when (failure is COMException or InvalidCastException)
                    {
                        Diagnostics.Log.Swallowed(failure);
                    }
                }
                _holding = tracked.Any(t => t.Element != 0);
                Placed before = _placed;
                _placed = new Placed(areas, places);
                if (!ReferenceEquals(before.For, areas) || !before.Places.SequenceEqual(places))
                {
                    Interlocked.Exchange(ref _movedAt, Environment.TickCount64);
                    // The overlay can be closing: its wait handle may be gone.
                    try { _moved(); }
                    catch (ObjectDisposedException closed) { Diagnostics.Log.Swallowed(closed); }
                }
                lock (_gate)
                {
                    _answered = asked;
                    Monitor.PulseAll(_gate);
                }
                Thread.Sleep(Soonest);
            }
        }
        finally
        {
            foreach (Tracked t in tracked) t.Release();
            if (walker != 0) Marshal.Release(walker);
            if (automation != 0) Marshal.Release(automation);
            lock (_gate)
            {
                _stopping = true;
                Monitor.PulseAll(_gate);
                _poke.Dispose();
            }
        }
    }

    private void Round(nint automation, nint walker, Tracked[] tracked, long asked, Rectangle?[] places)
    {
        if (!WindowInfo.TryGetFrameBounds(_window, out Rectangle frame, out _)) return;
        float scale = User32.GetDpiForWindow(_window) is var dpi and > 0 ? dpi / 96f : 1;
        long now = Environment.TickCount64;
        for (int i = 0; i < tracked.Length; i++)
        {
            Tracked t = tracked[i];
            if (!t.Area.Follows) continue;
            Rectangle? bounds = t.Element == 0 ? null : ElementFinder.BoundsOf(t.Element);
            // The control is gone, as when its page went: looked for again where it was marked.
            if (t.Element != 0 && bounds is null) t.Release();
            // Only when something changed since the last look: a still page would be walked every second for nothing.
            if (t.Element == 0 && now >= t.LookAt && asked != t.LookedFor)
            {
                t.LookAt = now + LookAgain;
                t.LookedFor = asked;
                t.Answered = Find(automation, walker, t, frame, scale);
                if (t.Element != 0) bounds = ElementFinder.BoundsOf(t.Element);
            }
            if (t.Element == 0 || bounds is not { } control)
            {
                places[i] = t.Answered ? Rectangle.Empty : null;
                continue;
            }
            if (control != t.Last || now >= t.ClipAt)
            {
                t.Clip = ClipOf(t.Clips);
                t.ClipAt = now + Check;
                t.Last = control;
            }
            Rectangle shown = Rectangle.Intersect(control, t.Clip);
            places[i] = shown.Width > 0 && shown.Height > 0 ? shown with { X = shown.X - frame.X, Y = shown.Y - frame.Y } : Rectangle.Empty;
        }
    }

    // Whether the app answered.
    private bool Find(nint automation, nint walker, Tracked t, Rectangle frame, float scale)
    {
        Rectangle marked = t.Area.In(frame.Size, scale);
        if (marked.IsEmpty) return true;
        marked.Offset(frame.Location);
        List<nint>? chain = ElementFinder.ChainAt(automation, walker, _window, Middle(marked));
        if (chain is null) return false;
        int best = Best(chain, marked, FoundAgain, t.Area);
        Rectangle? found = best < 0 ? null : ElementFinder.BoundsOf(chain[best]);
        for (int i = 0; i < chain.Count; i++)
        {
            if (i == best && found is not null)
                t.Element = chain[i];
            // What the control is seen through: a page's document is its view, a pane its scrolled panel. Any other
            // outer control only when it held the whole control: a page's boxes can be smaller than what they hold.
            else if (i < best && found is { } inside && (ElementFinder.ControlTypeOf(chain[i]) is WindowType or DocumentType or PaneType ||
                     ElementFinder.BoundsOf(chain[i]) is { } outer && outer.Contains(inside)))
                t.Clips.Add(chain[i]);
            else
                Marshal.Release(chain[i]);
        }
        return true;
    }

    // The control at that place that covers most nearly the same part, never the window's own; like, when given,
    // is what it must be.
    private static int Best(List<nint> chain, Rectangle part, float least, KeptArea? like)
    {
        int best = -1;
        float most = least;
        for (int i = 1; i < chain.Count; i++)
        {
            if (ElementFinder.BoundsOf(chain[i]) is not { } bounds) continue;
            float overlap = Overlap(bounds, part);
            if (like is not null && (ElementFinder.ControlTypeOf(chain[i]) != like.ControlType || ElementFinder.AutomationIdOf(chain[i]) != like.ControlId ||
                // A page's class names can change as it plays: then only a near-exact fit counts.
                ElementFinder.ClassNameOf(chain[i]) != like.ControlClass && overlap < SameControl))
                continue;
            if (overlap < most) continue;
            most = overlap;
            best = i;
        }
        return best;
    }

    internal static float Overlap(Rectangle a, Rectangle b)
    {
        Rectangle both = Rectangle.Intersect(a, b);
        if (both.Width <= 0 || both.Height <= 0) return 0;
        double shared = (double)both.Width * both.Height;
        return (float)(shared / ((double)a.Width * a.Height + (double)b.Width * b.Height - shared));
    }

    private static Rectangle ClipOf(List<nint> clips)
    {
        var clip = Rectangle.FromLTRB(int.MinValue / 2, int.MinValue / 2, int.MaxValue / 2, int.MaxValue / 2);
        foreach (nint outer in clips)
            if (ElementFinder.BoundsOf(outer) is { } bounds)
                clip = Rectangle.Intersect(clip, bounds);
        return clip;
    }

    private static Point Middle(Rectangle area) => new(area.Left + area.Width / 2, area.Top + area.Height / 2);

    private sealed class Tracked(KeptArea area)
    {
        public KeptArea Area { get; } = area;
        public nint Element;
        public readonly List<nint> Clips = [];
        public Rectangle Clip;
        public Rectangle? Last;
        public long ClipAt, LookAt;
        public long LookedFor = long.MinValue;
        public bool Answered;

        public void Release()
        {
            if (Element != 0) Marshal.Release(Element);
            Element = 0;
            foreach (nint outer in Clips) Marshal.Release(outer);
            Clips.Clear();
            Last = null;
            LookAt = 0;
        }
    }
}
