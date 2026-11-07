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
        Handle = (nint)long.Parse(_process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("Test target gave no handle."));
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
    public string? NextReport(int milliseconds = 3000) => _reports.TryTake(out string? line, milliseconds) ? line : null;

    public void SetColors(string colors)
    {
        _process.StandardInput.WriteLine("color " + colors);
        _process.StandardInput.Flush();
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
