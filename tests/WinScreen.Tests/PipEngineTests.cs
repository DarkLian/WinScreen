using System.Text.Json;
using WinScreen.Pip;
using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

public sealed class PipEngineTests
{
    [Theory]
    [InlineData(60, 60)] [InlineData(144, 60)] [InlineData(144, 144)]
    [InlineData(240, 144)] [InlineData(30, 60)]
    public void Deadline_pacer_tracks_rate_without_building_backlog(int source, int target)
    {
        var pacer = new PipFramePacer(1_000_000, target);
        int accepted = Enumerable.Range(0, source * 10).Count(i => pacer.Accept((long)(i * 1_000_000.0 / source)));
        Assert.InRange(accepted, Math.Min(source, target) * 10 - 1, Math.Min(source, target) * 10 + 1);
        Assert.True(pacer.Accept(100_000_000));
        Assert.False(pacer.Accept(100_000_001));
    }

    [Fact]
    public void Pacer_handles_jitter_rate_change_and_signed_wrap()
    {
        var pacer = new PipFramePacer(1_000_000, 144);
        long start = long.MaxValue - 20_000;
        for (int i = 0; i < 1000; i++)
            Assert.True(pacer.Accept(unchecked(start + (long)(i * 1_000_000.0 / 144) + (i % 2 == 0 ? 500 : -500))));
        pacer.SetRate(60);
        Assert.True(pacer.Accept(0));
        Assert.False(pacer.Accept(6000));
        Assert.True(pacer.Accept(16667));
    }

    private sealed class Owned(int value) : IDisposable
    {
        internal int Value { get; } = value;
        internal int Disposals;
        public void Dispose() => Disposals++;
    }
    [Fact]
    public void Thousand_frame_burst_keeps_latest_without_mutating_consumers_and_rejects_late_callbacks()
    {
        using var queue = new LatestFrame<Owned>();
        var held = new Owned(-1);
        queue.Publish(held);
        Assert.Same(held, queue.Take());
        var frames = Enumerable.Range(0, 1000).Select(i => new Owned(i)).ToArray();
        foreach (var frame in frames) queue.Publish(frame);
        Assert.Equal(999, queue.Dropped);
        Assert.Equal(0, held.Disposals);
        Assert.All(frames[..^1], f => Assert.Equal(1, f.Disposals));
        Assert.Same(frames[^1], queue.Take());
        queue.Dispose(); queue.Dispose();
        var late = new Owned(1000);
        Assert.False(queue.Publish(late));
        Assert.Equal(1, late.Disposals);
        held.Dispose(); frames[^1].Dispose();
    }

    [Fact]
    public void Circuit_allows_one_recreation_then_requires_explicit_cooled_retry()
    {
        var policy = new PipBackendPolicy();
        Assert.True(policy.Fail(0));
        Assert.False(policy.Pinned);
        Assert.False(policy.Fail(1));
        Assert.True(policy.Pinned);
        Assert.False(policy.Retry(9999, 1000));
        Assert.True(policy.Retry(10001, 1000));
        Assert.False(policy.Pinned);
        Assert.True(policy.Fail(10002));
        Assert.False(policy.Fail(10003));
    }

    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(168)] [InlineData(192)]
    public void Physical_client_pixels_are_not_scaled_twice(int dpi)
    {
        var physical = new Size(576 * dpi / 96, 324 * dpi / 96);
        Assert.Equal(physical, PipRenderGeometry.Target(physical, new(3840, 2160), 16384, 8_294_400));
        Assert.Equal(new Rectangle(0, 0, 11, 9), PipRenderGeometry.Crop(new(-3, -2, 14, 11), new(100, 100)));
        Assert.Equal(new Rectangle(0, 0, 100, 100), PipRenderGeometry.Crop(new(int.MaxValue, 0, int.MaxValue, 10), new(100, 100)));
    }

    [Fact]
    public void Allocations_have_dimension_and_pixel_bounds()
    {
        var target = PipRenderGeometry.Target(new(100000, 50000), new(3840, 2160), 8192, 1_000_000);
        Assert.True((long)target.Width * target.Height <= 1_000_000);
        Assert.InRange(target.Width, 1, 8192);
        Assert.Throws<ArgumentOutOfRangeException>(() => PipRenderGeometry.Target(Size.Empty, new(1, 1), 8192, 1_000_000));
    }

    [Theory]
    [InlineData("{}", PipBackendPreference.Dwm)]
    [InlineData("null", PipBackendPreference.Dwm)]
    [InlineData("[]", PipBackendPreference.Dwm)]
    [InlineData("true", PipBackendPreference.Dwm)]
    [InlineData("{\"Pip\":null}", PipBackendPreference.Dwm)]
    [InlineData("{\"Pip\":\"invalid\"}", PipBackendPreference.Dwm)]
    [InlineData("{\"Pip\":{\"Backend\":{},\"FrameRate\":\"invalid\",\"OpacityPercent\":null}}", PipBackendPreference.Dwm)]
    [InlineData("{\"Pip\":{\"Backend\":\"mystery\"}}", PipBackendPreference.Dwm)]
    [InlineData("{\"Pip\":{\"Backend\":999}}", PipBackendPreference.Dwm)]
    [InlineData("{\"Pip\":{\"Backend\":\"Wgc\",\"FrameRate\":99999,\"OpacityPercent\":-30}}", PipBackendPreference.Wgc)]
    public void Migration_preserves_Dwm_and_sanitizes_new_preferences(string json, PipBackendPreference expected)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, json);
            var settings = AppSettings.Load(path);
            Assert.Equal(expected, settings.Pip.Backend);
            Assert.InRange(settings.Pip.FrameRate, 0, 480);
            Assert.InRange(settings.Pip.OpacityPercent, 10, 100);
            Assert.False(settings.PipClickForwarding);
            Assert.False(settings.PipAutoMove);
            Assert.Equal("Ctrl+Alt+T", settings.Hotkeys[AppCommand.TogglePin]);
            Assert.Equal(expected, settings.Clone().Pip.Backend);
        }
        finally { File.Delete(path); }
        Assert.Equal(PipBackendPreference.Auto, new AppSettings().Pip.Backend);
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void Pip_choices_are_localized(string language)
    {
        try
        {
            UiText.Initialize(language);
            foreach (string label in new[] { "PiP rendering", "Frame limit", "Scaling", "Compatibility (DWM)",
                         "Advanced (WGC)", "PiP diagnostics", "Retry advanced backend" })
            {
                Assert.NotEqual(label, UiText.T(label));
                Assert.DoesNotContain("?", UiText.T(label));
            }
        }
        finally { UiText.Initialize("en"); }
    }
}
