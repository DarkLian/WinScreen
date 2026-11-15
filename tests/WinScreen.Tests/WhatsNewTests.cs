using WinScreen.Updates;
using Xunit;

namespace WinScreen.Tests;

/// <summary>What's new (11.8 part 1): offered once per release after an update, never on a first install or a local build.</summary>
public sealed class WhatsNewTests
{
    [Theory]
    [InlineData("1.0.0", "1.1.0", true)] // An update.
    [InlineData(null, "1.1.0", true)] // Settings from before the list kept no version: they come from an update.
    [InlineData("1.1.0-beta", "1.1.0", false)] // Already offered for this release's beta.
    [InlineData("1.1.0", "1.1.0", false)]
    public void It_is_offered_once_per_release_after_an_update(string? lastSeen, string current, bool offered) =>
        Assert.Equal(offered, WhatsNew.ShouldOffer(current, lastSeen, firstRun: false, published: true));

    [Fact]
    public void Not_on_a_first_install_nor_for_a_local_build_nor_without_a_list()
    {
        Assert.False(WhatsNew.ShouldOffer("1.1.0", null, firstRun: true, published: true));
        Assert.False(WhatsNew.ShouldOffer("1.1.0", "1.0.0", firstRun: false, published: false));
        Assert.False(WhatsNew.ShouldOffer("0.1.0", null, firstRun: false, published: true));
    }

    [Fact]
    public void A_local_build_leaves_the_published_release_its_offer()
    {
        // The owner runs local betas of 1.1.0, then the published 1.1.0 arrives.
        string? lastSeen = WhatsNew.Remember("1.1.0-beta.11", null, published: false);
        Assert.Null(lastSeen);
        Assert.True(WhatsNew.ShouldOffer("1.1.0", lastSeen, firstRun: false, published: true));
        Assert.Equal("1.1.0", WhatsNew.Remember("1.1.0", lastSeen, published: true));
        Assert.Equal("1.0.0", WhatsNew.Remember("1.1.0-beta.11", "1.0.0", published: false));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void Every_list_is_in_every_language_with_the_same_number_of_lines(string language)
    {
        string[] english = WhatsNew.For("1.1.0", "en")!;
        string[] lines = WhatsNew.For("1.1.0-beta.11", language)!;
        Assert.NotEmpty(english);
        Assert.Equal(english.Length, lines.Length);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }
}
