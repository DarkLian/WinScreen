using System.Collections.Concurrent;
using System.Diagnostics;

namespace WinScreen.Tests.Support;

/// <summary>A real window in a separate process, closed again when the test ends.</summary>
internal sealed class TargetProcess : IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _reports = [];

    public TargetProcess(Rectangle bounds, string colors = "C81E1E", string title = "WinScreen test target", string dpi = "permonitorv2", nint owner = 0)
    {
        Bounds = bounds;
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WinScreen.TestTarget.exe"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[]
                 {
                     "--left", bounds.Left.ToString(), "--top", bounds.Top.ToString(),
                     "--width", bounds.Width.ToString(), "--height", bounds.Height.ToString(),
                     "--colors", colors, "--title", title, "--dpi", dpi,
                 })
        {
            start.ArgumentList.Add(arg);
        }
        if (owner != 0) { start.ArgumentList.Add("--owner"); start.ArgumentList.Add(owner.ToInt64().ToString()); }

        _process = Process.Start(start) ?? throw new InvalidOperationException("Test target didn't start.");
        Handle = (nint)long.Parse(_process.StandardOutput.ReadLine() ??
                                  throw new InvalidOperationException("Test target gave no handle."));
        new Thread(() =>
        {
            while (_process.StandardOutput.ReadLine() is { } line)
                _reports.Add(line);
        }) { IsBackground = true }.Start();
    }

    public nint Handle { get; }
    public Rectangle Bounds { get; }
    public Point Center => new(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);

    /// <summary>Waits for what the window itself says happened to it, such as "click 300 150 real=True".</summary>
    public string? NextReport(int milliseconds = 3000) =>
        _reports.TryTake(out string? line, milliseconds) ? line : null;

    public void SetColors(string colors)
    {
        _process.StandardInput.WriteLine("color " + colors);
        _process.StandardInput.Flush();
    }

    /// <summary>Gives the window a real title bar and borders, which the plain target doesn't have.</summary>
    internal void UseFrame()
    {
        _process.StandardInput.WriteLine("frame");
        _process.StandardInput.Flush();
        if (NextReport() != "framed")
            throw new InvalidOperationException("Test target didn't take a frame.");

        // A newly framed window can briefly hold a whole-window region, which reads as a shape of its own.
        var clear = Stopwatch.StartNew();
        bool settled = Desktop.WaitFor(() =>
        {
            if (Windows.WindowInfo.ShapeOf(Handle) is not null)
                clear.Restart();
            return clear.ElapsedMilliseconds >= 80;
        });
        if (!settled)
            throw new InvalidOperationException("Test target's frame didn't settle.");
    }

    internal void EnableContextMenu()
    {
        _process.StandardInput.WriteLine("contextmenu");
        _process.StandardInput.Flush();
        if (NextReport() != "context-ready") throw new InvalidOperationException("Context menu did not initialize.");
    }

    internal bool HasExited => _process.HasExited;

    /// <summary>Shows a question the window owns, with a title bar, as "Save changes?" does, and returns it.</summary>
    internal nint ShowDialog()
    {
        _process.StandardInput.WriteLine("dialog");
        _process.StandardInput.Flush();
        string? report = NextReport();
        if (report is null || !report.StartsWith("dialog ", StringComparison.Ordinal))
            throw new InvalidOperationException("Test target didn't show a dialog.");
        return (nint)long.Parse(report[7..]);
    }

    /// <summary>Shows a small popup the window owns, at this screen position, and returns it.</summary>
    /// <param name="displayOnly">Click-through, as a browser's link text is.</param>
    /// <summary>An owned popup made by another UI thread of the target.</summary>
    internal nint ShowThreadPopup(Point at)
    {
        _process.StandardInput.WriteLine($"threadpopup {at.X} {at.Y}");
        _process.StandardInput.Flush();
        string? report = NextReport();
        if (report is null || !report.StartsWith("popup ", StringComparison.Ordinal))
            throw new InvalidOperationException("Test target didn't show a popup.");
        return (nint)long.Parse(report[6..]);
    }

    internal nint ShowPopup(Point at, bool displayOnly = false)
    {
        _process.StandardInput.WriteLine($"popup {at.X} {at.Y}" + (displayOnly ? " display" : ""));
        _process.StandardInput.Flush();
        string? report = NextReport();
        if (report is null || !report.StartsWith("popup ", StringComparison.Ordinal))
            throw new InvalidOperationException("Test target didn't show a popup.");
        return (nint)long.Parse(report[6..]);
    }

    /// <summary>Makes every move or resize take this long for the window to handle.</summary>
    internal void BeSlow(int milliseconds)
    {
        _process.StandardInput.WriteLine("slow " + milliseconds);
        _process.StandardInput.Flush();
        if (NextReport() != "slow")
            throw new InvalidOperationException("Test target didn't slow down.");
    }

    /// <summary>Adds a real control at (20, 30), 140 by 50, for anything that looks inside a window.</summary>
    internal void AddButton()
    {
        _process.StandardInput.WriteLine("button");
        _process.StandardInput.Flush();
        if (NextReport() != "button")
            throw new InvalidOperationException("Test target didn't add a button.");
    }

    internal void Animate(int rate)
    {
        _process.StandardInput.WriteLine("animate " + rate);
        _process.StandardInput.Flush();
    }

    internal void Resize(Size size)
    {
        _process.StandardInput.WriteLine($"size {size.Width} {size.Height}");
        _process.StandardInput.Flush();
    }

    internal void SetMinimumSize(Size size)
    {
        _process.StandardInput.WriteLine($"minimum {size.Width} {size.Height}");
        _process.StandardInput.Flush();
        if (NextReport() != "minimum") throw new InvalidOperationException("The minimum size was not applied.");
    }

    /// <summary>Makes the top of this borderless window answer as a title bar.</summary>
    internal void RecordMessages()
    {
        _process.StandardInput.WriteLine("recordmessages");
        _process.StandardInput.Flush();
        if (NextReport() != "recordmessages") throw new InvalidOperationException("Message recording did not start.");
    }

    /// <summary>Makes the window handle the mouse as a browser page does, and report the messages involved.</summary>
    internal void BehaveLikeBrowser()
    {
        _process.StandardInput.WriteLine("browserlike");
        _process.StandardInput.Flush();
        if (NextReport() != "browserlike") throw new InvalidOperationException("The target did not start behaving like a browser.");
    }

    internal void UseCaptionButton(int hit)
    {
        _process.StandardInput.WriteLine("captionhit " + hit);
        _process.StandardInput.Flush();
        if (NextReport() != "captionhit") throw new InvalidOperationException("The target has no caption button.");
    }

    public void UseTitleBar()
    {
        _process.StandardInput.WriteLine("titlebar");
        _process.StandardInput.Flush();
        if (NextReport() != "titlebar") throw new InvalidOperationException("The target has no title bar.");
    }

    public nint CreateUnattributedLayeredWindow()
    {
        _process.StandardInput.WriteLine("layered");
        _process.StandardInput.Flush();
        string report = NextReport() ?? throw new InvalidOperationException("No layered window was created.");
        return (nint)long.Parse(report.Split(' ')[1]);
    }

    /// <summary>A second main window of the same process, with a title bar; it reports "second-click x y" and "second-key k".</summary>
    public nint CreateSecondWindow(Rectangle bounds)
    {
        _process.StandardInput.WriteLine($"second {bounds.X} {bounds.Y} {bounds.Width} {bounds.Height}");
        _process.StandardInput.Flush();
        string report = NextReport() ?? throw new InvalidOperationException("No second window was created.");
        return (nint)long.Parse(report.Split(' ')[1]);
    }

    /// <summary>A plain native window in this process, which acts like an ordinary app window.</summary>
    /// <param name="framed">With a title bar and borders Windows draws, which, unlike a Windows Forms form's, keep a crop.</param>
    public nint CreatePlainWindow(Rectangle bounds, bool framed = false)
    {
        _process.StandardInput.WriteLine($"plain {bounds.X} {bounds.Y} {bounds.Width} {bounds.Height}" + (framed ? " framed" : ""));
        _process.StandardInput.Flush();
        string report = NextReport() ?? throw new InvalidOperationException("No plain window was created.");
        return (nint)long.Parse(report.Split(' ')[1]);
    }

    public void Hang()
    {
        _process.StandardInput.WriteLine("hang");
        _process.StandardInput.Flush();
        if (NextReport() != "hung") throw new InvalidOperationException("The target did not hang as requested.");
    }

    public void Unhang()
    {
        _process.StandardInput.WriteLine("unhang");
        _process.StandardInput.Flush();
        if (NextReport() != "responsive") throw new InvalidOperationException("The target did not resume.");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill();
            _process.WaitForExit(2000);
        }
        catch (InvalidOperationException)
        {
            // Already disposed by the test that closes it early.
        }

        _process.Dispose();
    }
}
