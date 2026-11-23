using static WinScreen.Localization.UiText;
using System.Diagnostics.CodeAnalysis;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;

namespace WinScreen.Pip;

public sealed partial class PipWindow
{
    private void OnAnswered(PressOutcome outcome)
    {
        if (_cleaned) return;
        if (outcome is PressOutcome.Delivered or PressOutcome.Popup) _offer = PressOffer.None;
        Offer(_pressFeedback.Note(outcome, Environment.TickCount64, CanOffer));
    }

    private bool CanOffer => _hideItem.Checked && !_usingOriginal && !ShowsOriginalOnly;

    internal void Offer(PressOffer offer)
    {
        if (offer == PressOffer.None || !CanOffer) return;
        _offer = offer;
        _offerUntil = Environment.TickCount64 + OfferShownFor;
        Diagnostics.Log.Event("press.offered", ("always", offer == PressOffer.AlwaysShowOriginal),
            ("program", Diagnostics.LogValue.ProgramOf(_source)));
        if (!_suspended && Visible) ShowState();
    }

    internal void OnChipChosen(PipChipState state)
    {
        if (state == PipChipState.SavedFile && _savedFile is { } file)
        {
            Pictures.ShowFile(file);
            _saidUntil = 0;
            _chip?.Hide();
            return;
        }
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
