using static WinScreen.Localization.UiText;
using System.Diagnostics.CodeAnalysis;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>Presses through the view that didn't get through, and the chip that offers the original instead.</summary>
public sealed partial class PipWindow
{
    /// <summary>
    /// A press through the view that didn't get through, said on the chip for a few seconds, with the original
    /// offered instead. A program that keeps refusing presses is offered a place in Settings' Show original list.
    /// </summary>
    private void OnAnswered(PressOutcome outcome)
    {
        if (_cleaned) return;
        if (outcome is PressOutcome.Delivered or PressOutcome.Popup) _offer = PressOffer.None; // The app answers again.
        Offer(_pressFeedback.Note(outcome, Environment.TickCount64, CanOffer));
    }

    /// <summary>An offer can be shown: the original is hidden and the program is one that is sent input.</summary>
    private bool CanOffer => _hideItem.Checked && !_usingOriginal && !ShowsOriginalOnly;

    /// <summary>Says on the chip, for a few seconds, that the original can be used instead.</summary>
    internal void Offer(PressOffer offer)
    {
        if (offer == PressOffer.None || !CanOffer) return;
        _offer = offer;
        _offerUntil = Environment.TickCount64 + OfferShownFor;
        Diagnostics.Log.Event("press.offered", ("always", offer == PressOffer.AlwaysShowOriginal),
            ("program", Diagnostics.LogValue.ProgramOf(_source)));
        if (!_suspended && Visible) ShowState();
    }

    /// <summary>A click on the chip: the one way to act on what it says.</summary>
    internal void OnChipChosen(PipChipState state)
    {
        if (state == PipChipState.SourceMinimized)
        {
            KeepUpdating();
            return;
        }
        if (state is not (PipChipState.OriginalOffered or PipChipState.AlwaysOriginalOffered)) return;
        bool always = state == PipChipState.AlwaysOriginalOffered && _program.Length > 0;
        Diagnostics.Log.Event("press.originalChosen", ("always", always), ("program", Diagnostics.LogValue.ProgramOf(_source)));
        _offer = PressOffer.None;
        _chip?.Hide();
        if (always) AlwaysShowOriginal?.Invoke(_program);
        UseOriginal();
    }
}
