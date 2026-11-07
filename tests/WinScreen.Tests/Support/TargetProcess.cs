using System.Collections.Concurrent;
using System.Diagnostics;

namespace WinScreen.Tests.Support;

/// <summary>A real window in a separate process, closed again when the test ends.</summary>
internal sealed class TargetProcess : IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _reports = [];

    public TargetProcess(Rectangle bounds, string colors = "C81E1E", string title = "WinScreen test target")
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
                     "--colors", colors, "--title", title,
                 })
        {
            start.ArgumentList.Add(arg);
        }

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

    /// <summary>Makes the top of this borderless window answer as a title bar.</summary>
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

    public void Hang()
    {
        _process.StandardInput.WriteLine("hang");
        _process.StandardInput.Flush();
        if (NextReport() != "hung") throw new InvalidOperationException("The target did not hang as requested.");
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
