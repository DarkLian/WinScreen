namespace WinScreen.Pip;

/// <summary>What a view offers after presses that didn't get through.</summary>
internal enum PressOffer { None, UseOriginal, AlwaysShowOriginal }

/// <summary>
/// Decides when a view says that a press didn't get through, and offers the original instead. A press that can't be
/// sent says so at once. One that may not have arrived must happen twice in a row, within 10 seconds, since a busy app
/// often catches up.
/// From the third offer for the same program, the offer is to always show its original, as Settings can.
/// </summary>
internal sealed class PipPressFeedback(string program, IDictionary<string, int> offers)
{
    /// <summary>Offers to use the original once before offering to list the program.</summary>
    internal const int OffersBeforeAlways = 2;

    private static readonly Dictionary<string, int> s_session = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Two presses that may not have arrived count as "in a row" only this close together.</summary>
    internal const long InARow = 10_000;

    private long _unconfirmedAt = -1;

    /// <summary>One for a view's program, counting offers over this run of WinScreen. Used on the UI thread only.</summary>
    public PipPressFeedback(string program) : this(program, s_session) { }

    /// <param name="now">Milliseconds, as <see cref="Environment.TickCount64"/> gives them.</param>
    /// <param name="canOffer">
    /// False when the view couldn't show an offer now (its original is on show, or nothing is sent to the program):
    /// the press still counts toward the next one, but no offer is made or counted.
    /// </param>
    public PressOffer Note(PressOutcome outcome, long now, bool canOffer = true)
    {
        switch (outcome)
        {
            case PressOutcome.Delivered or PressOutcome.Popup:
                _unconfirmedAt = -1;
                return PressOffer.None;
            case PressOutcome.Unconfirmed when _unconfirmedAt < 0 || now - _unconfirmedAt > InARow:
                _unconfirmedAt = now;
                return PressOffer.None;
            case PressOutcome.Unconfirmed or PressOutcome.Unsupported:
                _unconfirmedAt = -1;
                return canOffer ? Offer() : PressOffer.None;
            default:
                return PressOffer.None; // The view's own controls, its border, moving it and scrolling say nothing.
        }
    }

    private PressOffer Offer()
    {
        // Windows of many apps share a host program (Store apps' frame, the shell's), so listing it would list them all.
        if (program.Length == 0 || Compatibility.AppProfiles.IsSharedHost(program)) return PressOffer.UseOriginal;
        int count = offers.TryGetValue(program, out int before) ? before + 1 : 1;
        offers[program] = count;
        return count > OffersBeforeAlways ? PressOffer.AlwaysShowOriginal : PressOffer.UseOriginal;
    }
}
