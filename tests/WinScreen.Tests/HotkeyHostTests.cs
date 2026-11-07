using WinScreen.Hotkeys;
using WinScreen.Native;
using WinScreen.Tests.Support;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public class HotkeyHostTests
{
    private const uint Modifiers = User32.MOD_CONTROL | User32.MOD_ALT;

    private static void OnUiThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    [Trait("Category", "Interactive")]
    public void Real_key_press_reaches_the_app()
    {
        OnUiThread(() =>
        {
            using var host = new HotkeyHost();
            int pressed = 0;
            host.Pressed += id => pressed = id;
            Assert.True(host.TryRegister(7, Modifiers, Keys.F24, out string? failure), failure);

            Desktop.Press(Keys.F24, Keys.ControlKey, Keys.Menu);

            Assert.True(Desktop.WaitFor(() => pressed == 7, 2000, Application.DoEvents));
        });
    }

    [Fact]
    public void A_hotkey_already_taken_is_reported()
    {
        OnUiThread(() =>
        {
            using var first = new HotkeyHost();
            using var second = new HotkeyHost();
            Assert.True(first.TryRegister(8, Modifiers, Keys.F23, out _));

            Assert.False(second.TryRegister(8, Modifiers, Keys.F23, out string? failure));
            Assert.False(string.IsNullOrWhiteSpace(failure));
        });
    }
}
