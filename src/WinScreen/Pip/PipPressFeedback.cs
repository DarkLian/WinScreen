namespace WinScreen.Pip;

/// <summary>What a view offers after presses that didn't get through.</summary>
internal enum PressOffer { None, UseOriginal, AlwaysShowOriginal }

/// <summary>
/// Decides when a view says that a press didn't get through, and offers the original instead. A press that can't be
/// sent says so at once. One that may not have arrived must happen twice in a row, since a busy app often catches up.
/// From the third offer for the same program, the offer is to always show its original, as Settings can.
/// </summary>
internal sealed class PipPressFeedback(string program, IDictionary<string, int> offers)
{
    /// <summary>Offers to use the original once before offering to list the program.</summary>
    internal const int OffersBeforeAlways = 2;

    // Windows of many apps share these processes, so listing one would list them all.
    private static readonly string[] SharedHosts = ["ApplicationFrameHost"];

    private static readonly Dictionary<string, int> s_session = new(StringComparer.OrdinalIgnoreCase);

    private bool _unconfirmed;

    /// <summary>One for a view's program, counting offers over this run of WinScreen. Used on the UI thread only.</summary>
    public PipPressFeedback(string program) : this(program, s_session) { }

    public PressOffer Note(PressOutcome outcome)
    {
        switch (outcome)
        {
            case PressOutcome.Delivered or PressOutcome.Popup:
                _unconfirmed = false;
                return PressOffer.None;
            case PressOutcome.Unconfirmed when !_unconfirmed:
                _unconfirmed = true;
                return PressOffer.None;
            case PressOutcome.Unconfirmed or PressOutcome.Unsupported:
                _unconfirmed = false;
                return Offer();
            default:
                return PressOffer.None; // The view's own controls, its border, moving it and scrolling say nothing.
        }
    }

    private PressOffer Offer()
    {
        if (program.Length == 0 || SharedHosts.Contains(program, StringComparer.OrdinalIgnoreCase)) return PressOffer.UseOriginal;
        int count = offers.TryGetValue(program, out int before) ? before + 1 : 1;
        offers[program] = count;
        return count > OffersBeforeAlways ? PressOffer.AlwaysShowOriginal : PressOffer.UseOriginal;
    }
}
