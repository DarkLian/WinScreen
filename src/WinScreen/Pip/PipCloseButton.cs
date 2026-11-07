using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>
/// A close button for a PiP view. Nothing can be painted over a DWM thumbnail in the same window,
/// so this is a small window of its own. The view is topmost, and owning isn't enough to rise above
/// that: this has to be topmost as well or it sits behind the view, out of sight and out of reach.
/// </summary>
internal sealed class PipCloseButton : Form
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    public PipCloseButton(Form view, string name, int size)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(size, size);
        Owner = view;
        var button = new Button
        {
            Text = "",
            Font = new Font("Segoe MDL2 Assets", size / 2f, GraphicsUnit.Pixel),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(32, 32, 32),
            Dock = DockStyle.Fill,
            TabStop = false,
            AccessibleName = name,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
        button.Click += (_, _) => view.Close();
        Controls.Add(button);
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
