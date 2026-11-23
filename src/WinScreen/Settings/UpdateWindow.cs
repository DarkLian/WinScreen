using static WinScreen.Localization.UiText;

namespace WinScreen.Settings;

// What a check the person asked for is doing, from the first look to Restart now, in place of notifications. Checks
// WinScreen makes by itself never open it, but one under way shows here while it is open.
internal sealed class UpdateWindow : ThemedDialog
{
    internal const int Busy = -1;
    private readonly SoftLabel _says = new() { AutoSize = true, UseMnemonic = false, MinimumSize = new Size(420, 0), MaximumSize = new Size(420, 0) };
    private readonly ProgressLine _progress = new() { Width = 420 };
    private readonly PlainButton _action;
    private Action? _run;

    internal UpdateWindow() : base(T("updates.title"))
    {
        Whole(_says);
        Whole(_progress);
        CancelButton = AddAction(T("common.close"), false, Close);
        _action = AddAction(" ", true, () => _run?.Invoke());
        _action.Visible = false;
    }

    internal string Saying => _says.Text;

    internal string? Offered => _action.Visible ? _action.Text : null;

    internal int? Percent => _progress.Visible ? _progress.Percent : null;

    // Percent from 0 to 100, Busy while how far is unknown, or null for no bar.
    internal void Say(string text, int? percent = null)
    {
        _says.Text = text;
        _progress.Visible = percent is not null;
        _progress.Percent = percent ?? 0;
        _action.Visible = false;
        _run = null;
    }

    internal void Offer(string text, string action, Action run)
    {
        Say(text);
        _action.Text = action;
        _action.Size = _action.GetPreferredSize(Size.Empty);
        _action.Visible = true;
        _run = run;
        AcceptButton = _action;
    }

    // A thin bar in the accent colour; while how far is unknown a short piece of it runs along.
    private sealed class ProgressLine : Control
    {
        private readonly System.Windows.Forms.Timer _run = new() { Interval = 16 };
        private int _percent;

        internal ProgressLine()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = 12;
            AccessibleRole = AccessibleRole.ProgressBar;
            _run.Tick += (_, _) => Invalidate();
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int Percent
        {
            get => _percent;
            set
            {
                _percent = value;
                _run.Enabled = value == Busy && Visible;
                AccessibleDescription = value == Busy ? null : $"{value}%";
                Invalidate();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            _run.Enabled = _percent == Busy && Visible;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? SettingsStyle.Page);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int thick = SettingsStyle.Unit(this, 4), top = (Height - thick) / 2;
            Color rest = Tray.MenuStyle.Dark ? Color.FromArgb(0x50, 255, 255, 255) : Color.FromArgb(0x40, 0, 0, 0);
            using (var brush = new SolidBrush(rest)) Bar(g, brush, 0, Width, top, thick);
            using var fill = new SolidBrush(SettingsStyle.Accent);
            if (_percent == Busy)
            {
                int piece = Width / 4, at = (int)(Environment.TickCount64 / 3 % (Width + piece)) - piece;
                Bar(g, fill, Math.Max(0, at), Math.Min(Width, at + piece), top, thick);
            }
            else Bar(g, fill, 0, Width * Math.Clamp(_percent, 0, 100) / 100, top, thick);
        }

        private static void Bar(Graphics g, Brush brush, int from, int to, int top, int thick)
        {
            if (to - from < 1) return;
            using System.Drawing.Drawing2D.GraphicsPath path = SettingsStyle.Rounded(new Rectangle(from, top, to - from, thick), Math.Max(1, thick / 2));
            g.FillPath(brush, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _run.Dispose();
            base.Dispose(disposing);
        }
    }
}
