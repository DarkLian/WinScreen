using static WinScreen.Settings.UiText;
using WinScreen.Tray;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>Lets a person choose what a view shows without switching to it. It only exists while open.</summary>
internal sealed class PipSourcePicker : Form
{
    private readonly TextBox _search = new() { Dock = DockStyle.Top };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.None, HideSelection = false, ShowItemToolTips = true,
    };

    private readonly ListViewGroup _windowsGroup = new(T("Windows"));
    private readonly ListViewGroup _screensGroup = new(T("Displays"));
    private readonly Button _open = new() { Text = T("Open"), AutoSize = true, Enabled = false };
    private readonly ImageList _icons = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
    private readonly List<PipChoice> _windows;
    private readonly List<PipChoice> _screens = PipSourceList.Screens();

    public PipSourcePicker(Func<nint, bool> allowed)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = T("Choose what to show");
        Icon = TrayApplication.LoadIcon();
        ClientSize = new Size(460, 420);
        MinimumSize = new Size(320, 280);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        _windows = PipSourceList.Windows(allowed);

        Native.User32.SendTextMessage(_search.Handle, EM_SETCUEBANNER, 1, T("Search by title or program"));
        _list.SmallImageList = _icons;
        _list.Columns.Add("", 420);
        _list.Groups.AddRange([_windowsGroup, _screensGroup]);
        _list.SelectedIndexChanged += (_, _) => _open.Enabled = Selected is not null;
        _list.ItemActivate += (_, _) => Choose();
        _list.Resize += (_, _) => _list.Columns[0].Width = Math.Max(100, _list.ClientSize.Width - 4);
        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Down || _list.Items.Count == 0) return;
            e.Handled = true;
            _list.Focus();
            _list.Items[0].Selected = true;
        };

        var region = new Button { Text = T("Select a region..."), AutoSize = true };
        region.Click += (_, _) =>
        {
            string device = Selected?.Device ?? Screen.FromPoint(Cursor.Position).DeviceName;
            Close();
            RegionWanted?.Invoke(device);
        };
        _open.Click += (_, _) => Choose();
        var cancel = new Button { Text = T("Cancel"), AutoSize = true };
        cancel.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4),
        };
        buttons.Controls.AddRange([cancel, _open, region]);

        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(buttons);
        Fill();
        ResumeLayout(true);
    }

    private const int EM_SETCUEBANNER = 0x1501;

    public event Action<nint>? WindowChosen;
    public event Action<string>? ScreenChosen;

    /// <summary>The person wants to drag out a region of this display.</summary>
    public event Action<string>? RegionWanted;

    private PipChoice? Selected => _list.SelectedItems.Count == 0 ? null : (PipChoice)_list.SelectedItems[0].Tag!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icons.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            if (Selected is null && _list.Items.Count > 0)
                _list.Items[0].Selected = true;
            Choose();
        }
    }

    private void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (PipChoice choice in _windows.Where(choice => PipSourceList.Matches(choice, _search.Text)))
            _list.Items.Add(Row(choice, _windowsGroup));
        foreach (PipChoice choice in _screens.Where(choice => PipSourceList.Matches(choice, _search.Text)))
            _list.Items.Add(Row(choice, _screensGroup));
        _list.EndUpdate();
        _open.Enabled = false;
    }

    private ListViewItem Row(PipChoice choice, ListViewGroup group)
    {
        string note = choice.Minimized ? " " + T("(minimized, will be restored)") : "";
        return new ListViewItem($"{choice.Title} ({choice.Program}){note}", group)
        {
            Tag = choice, ToolTipText = choice.Title, ImageKey = IconFor(choice),
        };
    }

    private string IconFor(PipChoice choice)
    {
        if (choice.Window == 0 || _icons.Images.ContainsKey(choice.Program))
            return choice.Program;
        try
        {
            string path = WindowInfo.ExecutablePath(choice.Window);
            if (path.Length > 0 && Icon.ExtractAssociatedIcon(path) is { } icon)
            {
                using (icon) _icons.Images.Add(choice.Program, icon.ToBitmap());
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or
                                      System.Runtime.InteropServices.ExternalException)
        {
            // A row without an icon is still a row.
        }

        return choice.Program;
    }

    private void Choose()
    {
        if (Selected is not { } choice)
            return;
        Close();
        if (choice.Device is { } device)
            ScreenChosen?.Invoke(device);
        else
            WindowChosen?.Invoke(choice.Window);
    }
}
