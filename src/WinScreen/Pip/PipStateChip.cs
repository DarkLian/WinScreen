using static WinScreen.Localization.UiText;
using WinScreen.Native;

namespace WinScreen.Pip;

internal enum PipChipState { None, SourceMinimized, NoNewPicture, Starting, Fallback, Unavailable, OriginalOffered, AlwaysOriginalOffered }

/// <summary>
/// A small note on a view saying its picture may be out of date, or that a press didn't get through. Like the
/// title bar it's a window of its own, because nothing can be painted over the picture. A minimized source is a
/// fact, so it's always shown. No new frames can just mean the source is idle, so that waits until the pointer is
/// on the view. A press that didn't get through is said for a few seconds, with the original offered instead.
/// </summary>
internal sealed class PipStateChip : Form
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int Padding_ = 6;

    private PipChipState _state;

    public PipStateChip(Form view)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Owner = view;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(32, 32, 32);
        ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.White;
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised with the chip's state when a chip that offers an action is clicked.</summary>
    public event Action<PipChipState>? Chosen;

    internal static bool OffersAction(PipChipState state) =>
        state is PipChipState.SourceMinimized or PipChipState.OriginalOffered or PipChipState.AlwaysOriginalOffered;

    internal static PipChipState Decide(bool minimized, PipSessionState state, bool pointerOnView, bool busy,
        PressOffer offer = PressOffer.None)
    {
        if (busy) return PipChipState.None;
        if (minimized) return PipChipState.SourceMinimized;
        if (state == PipSessionState.Failed) return PipChipState.Unavailable;
        if (offer == PressOffer.UseOriginal) return PipChipState.OriginalOffered;
        if (offer == PressOffer.AlwaysShowOriginal) return PipChipState.AlwaysOriginalOffered;
        if (state is PipSessionState.Starting or PipSessionState.Recovering) return PipChipState.Starting;
        if (state == PipSessionState.Fallback) return PipChipState.Fallback;
        return state == PipSessionState.SourcePaused && pointerOnView ? PipChipState.NoNewPicture : PipChipState.None;
    }

    internal static string Words(PipChipState state) => state switch
    {
        PipChipState.SourceMinimized => T("pip.state.sourceMinimized"),
        PipChipState.NoNewPicture => T("pip.state.noNewPicture"),
        PipChipState.Starting => T("pip.state.starting"),
        PipChipState.Fallback => T("pip.state.fallback"),
        PipChipState.Unavailable => T("common.unavailable"),
        PipChipState.OriginalOffered => T("pip.state.pressNotDelivered"),
        PipChipState.AlwaysOriginalOffered => T("pip.state.pressNotDeliveredAgain"),
        _ => "",
    };

    public void Show(PipChipState state, Point bottomLeft)
    {
        if (state != _state)
        {
            _state = state;
            Text = Words(state);
            Size text = TextRenderer.MeasureText(Text, Font);
            ClientSize = new Size(text.Width + 2 * Padding_, text.Height + Padding_);
            Cursor = OffersAction(state) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        Location = new Point(bottomLeft.X, bottomLeft.Y - Height);
        if (Visible) return;
        Show();
        WinScreen.Windows.TopLayer.Raise(Handle);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST);
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && OffersAction(_state))
            Chosen?.Invoke(_state);
    }

    /// <summary>What the chip says now, or None while it's hidden.</summary>
    internal PipChipState Saying => Visible ? _state : PipChipState.None;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }
}
