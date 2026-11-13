using WinScreen.Pip;
using Xunit;

namespace WinScreen.Tests;

public sealed class PipPressFeedbackTests
{
    private static PipPressFeedback For(string program, Dictionary<string, int>? offers = null) =>
        new(program, offers ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void A_press_that_cannot_be_sent_offers_the_original_at_once()
    {
        Assert.Equal(PressOffer.UseOriginal, For("calc").Note(PressOutcome.Unsupported));
    }

    [Fact]
    public void One_unconfirmed_press_says_nothing_and_two_in_a_row_offer_the_original()
    {
        PipPressFeedback feedback = For("winword");
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed));
        Assert.Equal(PressOffer.UseOriginal, feedback.Note(PressOutcome.Unconfirmed));
        // The offer starts the count again.
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed));
    }

    [Theory]
    [InlineData(PressOutcome.Delivered)]
    [InlineData(PressOutcome.Popup)]
    public void A_press_that_arrives_in_between_starts_the_count_again(PressOutcome between)
    {
        PipPressFeedback feedback = For("winword");
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed));
        Assert.Equal(PressOffer.None, feedback.Note(between));
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed));
    }

    [Theory]
    [InlineData(PressOutcome.CaptionButton)]
    [InlineData(PressOutcome.MovedView)]
    [InlineData(PressOutcome.ScrollMode)]
    [InlineData(PressOutcome.ViewControl)]
    [InlineData(PressOutcome.Outside)]
    public void The_views_own_presses_never_offer_anything_nor_break_a_run(PressOutcome own)
    {
        PipPressFeedback feedback = For("winword");
        Assert.Equal(PressOffer.None, feedback.Note(own));
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed));
        Assert.Equal(PressOffer.None, feedback.Note(own));
        Assert.Equal(PressOffer.UseOriginal, feedback.Note(PressOutcome.Unconfirmed));
    }

    [Fact]
    public void From_the_third_offer_for_a_program_the_offer_is_to_always_show_its_original()
    {
        var offers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Over several views of the same program in one run.
        Assert.Equal(PressOffer.UseOriginal, For("Calc", offers).Note(PressOutcome.Unsupported));
        Assert.Equal(PressOffer.UseOriginal, For("calc", offers).Note(PressOutcome.Unsupported));
        Assert.Equal(PressOffer.AlwaysShowOriginal, For("CALC", offers).Note(PressOutcome.Unsupported));
        Assert.Equal(PressOffer.AlwaysShowOriginal, For("calc", offers).Note(PressOutcome.Unsupported));
        // Another program counts on its own.
        Assert.Equal(PressOffer.UseOriginal, For("notepad", offers).Note(PressOutcome.Unsupported));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ApplicationFrameHost")]
    [InlineData("applicationframehost")]
    public void A_program_that_cannot_be_listed_alone_is_only_offered_its_original(string program)
    {
        var offers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int time = 0; time < 5; time++)
            Assert.Equal(PressOffer.UseOriginal, For(program, offers).Note(PressOutcome.Unsupported));
    }
}
