using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

public sealed class LocalizationTests : IDisposable
{
    public void Dispose() => UiText.Initialize("en");

    [Theory]
    [InlineData("zh-Hans", "en-US", "zh-Hans", "设置")]
    [InlineData("zh-Hant", "en-US", "zh-Hant", "設定")]
    [InlineData("system", "zh-CN", "zh-Hans", "设置")]
    [InlineData("system", "zh-SG", "zh-Hans", "设置")]
    [InlineData("system", "zh-TW", "zh-Hant", "設定")]
    [InlineData("system", "zh-HK", "zh-Hant", "設定")]
    [InlineData("system", "zh-MO", "zh-Hant", "設定")]
    [InlineData("en", "zh-TW", "en", "Settings")]
    [InlineData("unknown", "zh-TW", "en", "Settings")]
    public void Language_choice_overrides_system_and_falls_back_to_English(string choice, string system,
        string expected, string settings)
    {
        UiText.Initialize(choice, system);
        Assert.Equal(expected, UiText.Language);
        Assert.Equal(settings, UiText.T("Settings"));
        Assert.Equal("ExampleProgram.exe", UiText.T("ExampleProgram.exe"));
        Assert.Equal("Ctrl+Alt+R", UiText.T("Ctrl+Alt+R"));
    }

    [Theory]
    [InlineData("zh-Hans", "示例: 置顶, 不透明度 50%, 深色")]
    [InlineData("zh-Hant", "示例: 置頂, 不透明度 50%, 深色")]
    public void Saved_setups_translate_without_changing_program_names(string language, string expected)
    {
        UiText.Initialize(language);
        Assert.Equal(expected, new ProgramSetup { Program = "示例", Pinned = true, Alpha = 128, Dark = true }.ToString());
        Assert.DoesNotContain("{0}", string.Format(UiText.T("{0} window(s) refused the change."), 3));
        Assert.NotEqual("Another app already uses: ", UiText.T("Another app already uses: "));
    }

    [Theory]
    [InlineData("zh-Hans", "其他应用已占用： ", "WinScreen 浮动锚点 - ")]
    [InlineData("zh-Hant", "其他應用程式已占用： ", "WinScreen 浮動錨點 - ")]
    public void Translated_prefixes_preserve_the_spaces_before_dynamic_content(string language, string conflictPrefix,
        string anchorPrefix)
    {
        UiText.Initialize(language);
        Assert.Equal(conflictPrefix + "Ctrl+Alt+T", UiText.T("Another app already uses: ") + "Ctrl+Alt+T");
        Assert.Equal(anchorPrefix + "ExampleProgram", UiText.T("WinScreen anchor - ") + "ExampleProgram");
    }
}
