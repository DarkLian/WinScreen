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
        Assert.Equal(PressOffer.UseOriginal, For("calc").Note(PressOutcome.Unsupported, 0));
    }

    [Fact]
    public void One_unconfirmed_press_says_nothing_and_two_in_a_row_offer_the_original()
    {
        PipPressFeedback feedback = For("winword");
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed, 0));
        Assert.Equal(PressOffer.UseOriginal, feedback.Note(PressOutcome.Unconfirmed, 0));
        // The offer starts the count again.
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed, 0));
    }

    [Theory]
    [InlineData(PressOutcome.Delivered)]
    [InlineData(PressOutcome.Popup)]
    public void A_press_that_arrives_in_between_starts_the_count_again(PressOutcome between)
    {
        PipPressFeedback feedback = For("winword");
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed, 0));
        Assert.Equal(PressOffer.None, feedback.Note(between, 0));
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed, 0));
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
        Assert.Equal(PressOffer.None, feedback.Note(own, 0));
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed, 0));
        Assert.Equal(PressOffer.None, feedback.Note(own, 0));
        Assert.Equal(PressOffer.UseOriginal, feedback.Note(PressOutcome.Unconfirmed, 0));
    }

    [Fact]
    public void From_the_third_offer_for_a_program_the_offer_is_to_always_show_its_original()
    {
        var offers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Over several views of the same program in one run.
        Assert.Equal(PressOffer.UseOriginal, For("Calc", offers).Note(PressOutcome.Unsupported, 0));
        Assert.Equal(PressOffer.UseOriginal, For("calc", offers).Note(PressOutcome.Unsupported, 0));
        Assert.Equal(PressOffer.AlwaysShowOriginal, For("CALC", offers).Note(PressOutcome.Unsupported, 0));
        Assert.Equal(PressOffer.AlwaysShowOriginal, For("calc", offers).Note(PressOutcome.Unsupported, 0));
        // Another program counts on its own.
        Assert.Equal(PressOffer.UseOriginal, For("notepad", offers).Note(PressOutcome.Unsupported, 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ApplicationFrameHost")]
    [InlineData("applicationframehost")]
    [InlineData("explorer")] // The shell: desktop, taskbar and every folder window.
    public void A_program_that_cannot_be_listed_alone_is_only_offered_its_original(string program)
    {
        var offers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int time = 0; time < 5; time++)
            Assert.Equal(PressOffer.UseOriginal, For(program, offers).Note(PressOutcome.Unsupported, 0));
    }

    [Fact]
    public void Two_unconfirmed_presses_far_apart_are_not_in_a_row()
    {
        PipPressFeedback feedback = For("winword");
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed, 1_000));
        Assert.Equal(PressOffer.None, feedback.Note(PressOutcome.Unconfirmed, 1_000 + PipPressFeedback.InARow + 1));
        Assert.Equal(PressOffer.UseOriginal, feedback.Note(PressOutcome.Unconfirmed, 1_000 + PipPressFeedback.InARow + 500));
    }

    [Fact]
    public void An_offer_the_view_cannot_show_is_neither_made_nor_counted()
    {
        var offers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int time = 0; time < 5; time++)
            Assert.Equal(PressOffer.None, For("calc", offers).Note(PressOutcome.Unsupported, 0, canOffer: false));
        Assert.Empty(offers);
        Assert.Equal(PressOffer.UseOriginal, For("calc", offers).Note(PressOutcome.Unsupported, 0));
    }
}
