namespace WinScreen.Pip;

internal enum PressOffer { None, UseOriginal, AlwaysShowOriginal }

// A busy app often catches up, so a press that may not have arrived must happen twice in a row before anything is said.
internal sealed class PipPressFeedback(string program, IDictionary<string, int> offers)
{
    internal const int OffersBeforeAlways = 2;

    private static readonly Dictionary<string, int> s_session = new(StringComparer.OrdinalIgnoreCase);

    internal const long InARow = 10_000;

    private long _unconfirmedAt = -1;

    // UI thread only.
    public PipPressFeedback(string program) : this(program, s_session) { }

    // canOffer false (original on show, or nothing is sent to the program): the press still counts toward the next
    // offer, but none is made or counted.
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
        // Windows of many apps share a host program (Store apps' frame, the shell's), so listing it would list them
        // all.
        if (program.Length == 0 || Compatibility.AppProfiles.IsSharedHost(program)) return PressOffer.UseOriginal;
        int count = offers.TryGetValue(program, out int before) ? before + 1 : 1;
        offers[program] = count;
        return count > OffersBeforeAlways ? PressOffer.AlwaysShowOriginal : PressOffer.UseOriginal;
    }
}
