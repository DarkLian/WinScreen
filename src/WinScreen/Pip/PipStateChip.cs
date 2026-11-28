using static WinScreen.Localization.UiText;
using WinScreen.Native;

namespace WinScreen.Pip;

// Said: a moment's word after a copy; SavedFile: the same, and a click shows the file.
internal enum PipChipState { None, SourceMinimized, NoNewPicture, Starting, Fallback, Unavailable, OriginalOffered, AlwaysOriginalOffered, Said, SavedFile }

// A window of its own, like the title bar, because nothing can be painted over the picture.
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

    public event Action<PipChipState>? Chosen;

    internal static bool OffersAction(PipChipState state) =>
        state is PipChipState.SourceMinimized or PipChipState.OriginalOffered or PipChipState.AlwaysOriginalOffered or PipChipState.SavedFile;

    internal static PipChipState Decide(bool minimized, PipSessionState state, bool pointerOnView, bool busy,
        PressOffer offer = PressOffer.None, PipChipState said = PipChipState.None)
    {
        if (busy) return PipChipState.None;
        if (said is PipChipState.Said or PipChipState.SavedFile) return said;
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

    public void Show(PipChipState state, Point bottomLeft, string said = "")
    {
        string words = state is PipChipState.Said or PipChipState.SavedFile ? said : Words(state);
        if (state != _state || words != Text)
        {
            _state = state;
            Text = words;
            Fit();
            Cursor = OffersAction(state) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        Location = new Point(bottomLeft.X, bottomLeft.Y - Height);
        if (Visible) return;
        Show();
        // Shown on a display at another scale, its words are now drawn at that scale: measured again there.
        if (Fit()) Location = new Point(bottomLeft.X, bottomLeft.Y - Height);
        if (Owner is { } view) WinScreen.Windows.TopLayer.RaiseOver(Handle, view.Handle);
    }

    private bool Fit()
    {
        Size text = TextRenderer.MeasureText(Text, Font);
        int padding = Dpi.Scale(Padding_, DeviceDpi);
        Size wanted = new(text.Width + 2 * padding, text.Height + padding);
        if (ClientSize == wanted) return false;
        ClientSize = wanted;
        return true;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Fit();
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
