using static WinScreen.Localization.UiText;
using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Pip;

// Taking things out of a view: its picture (copied, saved, or kept as a frozen copy) and its text. And covers: parts
// hidden in the view itself, so neither it, its copies nor sharing its window ever show them.
public sealed partial class PipWindow
{
    // In the source's pixels from the visible corner of the window, as the crop is: zoom, crop, turn, size and the
    // way the view is drawn all leave them where they are.
    private List<Rectangle> _covers = [];
    private PipChipState _saying;
    private string _said = "";
    private long _saidUntil;
    private string? _savedFile;
    private const long SaidFor = 2500;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal PictureShelf Pictures { get; set; } = new();

    internal IReadOnlyList<Rectangle> Covers => _covers;

    // In the source's surface pixels, where the crop and the backends work.
    private Rectangle[] SurfaceCovers() => ViewCovers.Moved(_covers, _whole.Location);

    private void PushCovers() => _thumbnail?.SetCovers(SurfaceCovers());

    internal void SetCovers(IEnumerable<Rectangle> covers)
    {
        _covers = covers.Where(cover => cover.Width > 0 && cover.Height > 0).Distinct().Take(ViewCovers.Most).ToList();
        PushCovers();
        if (_thumbnail is not null && !_lastShown.IsEmpty) Attempt(() => _thumbnail.SetViewport(PictureArea, _lastShown));
        Diagnostics.Log.Event("view.covers", ("count", _covers.Count));
    }

    internal string SaidOnView => Environment.TickCount64 < _saidUntil ? _said : "";

    private void Say(string words, string? file = null)
    {
        _said = words;
        _savedFile = file;
        _saying = file is null ? PipChipState.Said : PipChipState.SavedFile;
        _saidUntil = Environment.TickCount64 + (file is null ? SaidFor : 2 * SaidFor);
        if (!_cleaned && !IsDisposed && Visible) ShowState();
    }

    // What the view shows as a picture: crop and zoom, covers blacked out, turned as it is; never the screen.
    internal async Task<Bitmap?> TakePictureAsync()
    {
        for (int tries = 0; tries < 30 && !IsDisposed; tries++)
        {
            if (_thumbnail is not { } view) return null;
            Size size = view.SourceSize;
            Rectangle shown = ShownPart(size);
            Rectangle[] covers = SurfaceCovers();
            ViewTurn turn = _appliedTurn;
            if (view.TakePicture() is { } capture)
            {
                using Bitmap? whole = await Within(capture.Picture, TimeSpan.FromSeconds(3));
                await UiThread.Back(this);
                return whole is null ? null : ViewPicture.Compose(whole, capture.Origin, shown, covers, turn);
            }
            if (_origin.IsWindow)
            {
                nint window = _source;
                PipSource origin = _origin;
                using (Bitmap? printed = await Task.Run(() => ViewPicture.Print(window, size)))
                    if (printed is not null) return ViewPicture.Compose(printed, Point.Empty, shown, covers, turn);
                (Bitmap Picture, Point Origin)? once = await Task.Run(() => ViewPicture.CaptureOnce(origin, size));
                await UiThread.Back(this);
                if (once is { } captured)
                    using (captured.Picture)
                        return ViewPicture.Compose(captured.Picture, captured.Origin, shown, covers, turn);
                return null;
            }
            // A display or region is drawn by capture alone, which is still starting.
            await Task.Delay(100);
            await UiThread.Back(this);
        }
        return null;
    }

    private static async Task<Bitmap?> Within(Task<Bitmap?> picture, TimeSpan limit)
    {
        try { return await picture.WaitAsync(limit); }
        catch (TimeoutException)
        {
            _ = picture.ContinueWith(late => late.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            return null;
        }
    }

    internal async void CopyPicture()
    {
        try
        {
            await CopyPictureAsync();
        }
        catch (Exception failure) { Unexpected(failure); }
    }

    // An async void that throws ends WinScreen: whatever goes wrong here is told and logged instead.
    private void Unexpected(Exception failure)
    {
        Diagnostics.Log.Error("view.pictureFailed", failure);
        if (!_cleaned && !IsDisposed) Problem?.Invoke(string.Format(T("errors.pip.copyFailed"), failure.Message));
    }

    private async Task CopyPictureAsync()
    {
        try
        {
            using Bitmap? picture = await TakePictureAsync();
            await UiThread.Back(this);
            if (_cleaned || IsDisposed) return;
            if (picture is null) { Problem?.Invoke(T("errors.pip.noPicture")); return; }
            (string? file, string? problem) = Pictures.Keep(picture);
            if (problem is not null) Problem?.Invoke(string.Format(T("errors.pip.saveFailed"), problem));
            bool copied = Pictures.Copies();
            if (copied || file is not null)
                Say(file is null ? T("pip.state.pictureCopied") : copied ? T("pip.state.pictureSaved") : T("pip.state.pictureOnlySaved"), file);
            Diagnostics.Log.Event("view.pictureCopied", ("copied", copied), ("saved", file is not null), ("covers", _covers.Count));
        }
        catch (System.Runtime.InteropServices.ExternalException failure)
        {
            Problem?.Invoke(string.Format(T("errors.pip.copyFailed"), failure.Message));
        }
    }

    internal event Action<StillPicture>? StillOpened;

    internal async void FreezeCopy()
    {
        try { await FreezeCopyAsync(); }
        catch (Exception failure) { Unexpected(failure); }
    }

    private async Task FreezeCopyAsync()
    {
        Bitmap? picture = await TakePictureAsync();
        await UiThread.Back(this);
        if (_cleaned || IsDisposed) { picture?.Dispose(); return; }
        if (picture is null) { Problem?.Invoke(T("errors.pip.noPicture")); return; }
        var still = new StillPicture(picture, Pictures, _origin.Title);
        still.Problem += text => Problem?.Invoke(text);
        Rectangle shown = RectangleToScreen(PictureArea);
        still.ShowBeside(Bounds, Math.Max(shown.Width, shown.Height) + 2 * PipGeometry.Border);
        StillOpened?.Invoke(still);
        Diagnostics.Log.Event("view.frozenCopy", ("covers", _covers.Count));
    }

    internal ViewAreaPicker? AreaPicker { get; private set; }

    // Copy text's and Cover parts' overlays with their instructions, lowest first. Read from other threads (the tray's
    // list of windows kept on top), so kept as handles. While one is open the view stays under it and leaves the
    // pointer to it: no visit, title bar or preview.
    private volatile nint[] _pickerWindows = [];
    internal IReadOnlyList<nint> PickerWindows => _pickerWindows;
    private bool Picking => AreaPicker is not null || CoverEditor is not null;

    private void Picked(Form overlay)
    {
        _pickerWindows = [overlay.Handle, .. overlay.OwnedForms.Where(owned => owned.IsHandleCreated).Select(owned => owned.Handle)];
        _titleBar?.Dismiss();
        DropHoverPreview();
        StayOnTop();
    }

    // A part dragged over, or all the view's text for a click.
    internal void CopyText()
    {
        EndLive();
        CancelPress();
        Rectangle area = PictureArea;
        var picker = new ViewAreaPicker(RectangleToScreen(area), T("pip.copyText.title"), T("pip.copyText.instructions"));
        picker.FormClosed += (_, _) => { if (AreaPicker == picker) { AreaPicker = null; _pickerWindows = []; } };
        picker.Chosen += async part =>
        {
            try
            {
                using Bitmap? picture = await TakePictureAsync();
                await UiThread.Back(this);
                if (_cleaned || IsDisposed) return;
                if (picture is null) { Problem?.Invoke(T("errors.pip.noPicture")); return; }
                Rectangle chosen = part.IsEmpty ? new Rectangle(Point.Empty, picture.Size)
                    : ViewPicture.InPicture(part with { X = part.X + area.X, Y = part.Y + area.Y }, area, picture.Size);
                if (chosen.Width <= 0 || chosen.Height <= 0) return;
                using Bitmap piece = picture.Clone(chosen, picture.PixelFormat);
                string? said = await ViewText.CopyFrom(this, piece, Pictures, text => Problem?.Invoke(text));
                if (said is not null && !_cleaned && !IsDisposed) Say(said);
            }
            catch (Exception failure) { Unexpected(failure); }
        };
        AreaPicker = picker;
        picker.ShowOnTop();
        Picked(picker);
    }

    internal KeptAreaOverlay? CoverEditor { get; private set; }

    // Covers outside what the view shows now stay as they are; one partly outside stays whole unless it's changed.
    internal void EditCovers()
    {
        if (_thumbnail is null || _lastShown.IsEmpty) return;
        EndLive();
        CancelPress();
        Rectangle area = PictureArea;
        var mapping = new PipMapping(area, _lastShown, _appliedTurn);
        Rectangle[] surface = SurfaceCovers();
        var shownAs = new Dictionary<Rectangle, Rectangle>();
        var kept = new List<Rectangle>();
        for (int i = 0; i < _covers.Count; i++)
        {
            if (!surface[i].IntersectsWith(_lastShown)) { kept.Add(_covers[i]); continue; }
            Rectangle inView = mapping.ToView(surface[i]);
            shownAs.TryAdd(inView with { X = inView.X - area.X, Y = inView.Y - area.Y }, _covers[i]);
        }
        var editor = new KeptAreaOverlay(RectangleToScreen(area), shownAs.Keys, ViewCovers.Most - kept.Count,
            T("pip.covers.title"), T("pip.covers.instructions"), Color.Black);
        editor.FormClosed += (_, _) => { if (CoverEditor == editor) { CoverEditor = null; _pickerWindows = []; } };
        editor.Done += marked =>
        {
            if (IsDisposed || _thumbnail is null) return;
            var covers = new List<Rectangle>(kept);
            foreach (Rectangle part in marked)
            {
                if (shownAs.TryGetValue(part, out Rectangle before)) { covers.Add(before); continue; }
                Rectangle inSource = new PipMapping(area, _lastShown, _appliedTurn)
                    .SelectionToSource(part with { X = part.X + area.X, Y = part.Y + area.Y });
                if (inSource.Width > 0 && inSource.Height > 0)
                    covers.Add(inSource with { X = inSource.X - _whole.X, Y = inSource.Y - _whole.Y });
            }
            SetCovers(covers);
        };
        CoverEditor = editor;
        editor.ShowOnTop();
        Picked(editor);
    }

    // Covers for a preset: shares of the whole source, so they fit another window of another size.
    private List<PipCropShares>? CoverShares(Size whole) => _covers.Count == 0 ? null
        : _covers.Select(cover => PipCropShares.Of(cover, whole)).OfType<PipCropShares>().ToList();
}

// What follows an await touches windows and the clipboard, so it comes back to the window's own thread explicitly:
// an await alone doesn't, on a thread without WinForms' own message loop (seen: a copy's "Picture copied" made its
// note on a pool thread and hung there).
internal static class UiThread
{
    internal static Turn Back(Control control) => new(control);

    internal readonly struct Turn(Control control) : System.Runtime.CompilerServices.INotifyCompletion
    {
        public Turn GetAwaiter() => this;
        public bool IsCompleted => control.IsDisposed || !control.IsHandleCreated || !control.InvokeRequired;
        public void OnCompleted(Action continuation)
        {
            try { control.BeginInvoke(continuation); }
            catch (Exception failure) when (failure is InvalidOperationException or ObjectDisposedException)
            {
                // The window is gone: the work still ends, off its thread, so it lets go of what it holds; callers
                // check IsDisposed before touching the window or the clipboard.
                Diagnostics.Log.Swallowed(failure);
                ThreadPool.QueueUserWorkItem(_ => continuation());
            }
        }
        public void GetResult() { }
    }
}

internal static class ViewText
{
    // What to say on the picture afterwards, or null when a problem was told instead.
    internal static async Task<string?> CopyFrom(Control owner, Bitmap picture, PictureShelf shelf, Action<string> problem)
    {
        string text;
        try
        {
            text = (await PictureText.Read(picture)).Trim();
            await UiThread.Back(owner);
        }
        catch (PictureText.NoReaderException)
        {
            problem(T("errors.pip.noTextReader"));
            return null;
        }
        catch (Exception failure) when (failure is System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException)
        {
            problem(string.Format(T("errors.pip.textFailed"), failure.Message));
            return null;
        }
        if (owner.IsDisposed) return null;
        if (text.Length == 0) return T("pip.state.noText");
        try { shelf.PutText(text); }
        catch (System.Runtime.InteropServices.ExternalException failure)
        {
            problem(string.Format(T("errors.pip.copyFailed"), failure.Message));
            return null;
        }
        Diagnostics.Log.Event("view.textCopied", ("characters", text.Length));
        return T("pip.state.textCopied");
    }
}
