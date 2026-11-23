using static WinScreen.Localization.UiText;

namespace WinScreen.Pip;

// Recording what the view shows to a video file. The frames are the capture's own, covers in, crop, zoom and turn as
// shown, never the screen; so a view drawn by Windows' thumbnail is drawn by capture while it records.
public sealed partial class PipWindow
{
    private ViewRecording? _recording;
    private PipRecordingMark? _recordingMark;

    internal ViewRecording? ActiveRecording => _recording;

    internal RecordingResult? LastRecording { get; private set; }

    internal event Action<RecordingResult>? Recorded;

    // Why this view can't record now, or null when it can.
    internal string? RecordingRefusedBecause()
    {
        if (!ViewRecording.Supported) return T("pip.record.noEncoder");
        if (_origin.IsWindow && (_parkedCaptureFallback || _windows.IsParked(_source) && !CaptureBorder.TryHide(_ => { }, wait: 0)))
            return T("pip.record.notHidden");
        if (_origin.IsWindow && Environment.OSVersion.Version.Build < FirstBuildCapturingSwitcherHiddenWindows && _windows.IsOutOfSwitcher(_source))
            return T("pip.record.notOutOfSwitcher");
        if (_thumbnail is { CanCapture: false }) return T("pip.record.noCapture");
        return null;
    }

    internal void ToggleRecording()
    {
        if (_recording is not null) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        if (_thumbnail is null || _recording is not null || _cleaned) return;
        if (RecordingRefusedBecause() is { } reason)
        {
            Problem?.Invoke(reason);
            return;
        }
        string path;
        try { path = Pictures.NewRecordingPath(); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Problem?.Invoke(string.Format(T("errors.pip.recordFailed"), failure.Message));
            return;
        }
        uint sound = _origin.IsWindow && Pictures.RecordsSound() ? AppSoundCapture.ProcessOf(_source) : 0;
        var recording = new ViewRecording(path, sound) { Muted = sound != 0 && SoundState?.Invoke() == true };
        _recording = recording;
        _thumbnail.Configure(BackendPreferences());
        _thumbnail.Record(recording);
        FollowRecordingMark();
        FollowRecording(recording);
        Diagnostics.Log.Event("view.recording", ("on", true), ("sound", sound != 0), ("covers", _covers.Count),
            ("window", _origin.IsWindow));
    }

    internal void StopRecording()
    {
        if (_recording is not { } recording) return;
        recording.Finish();
        LetGoOfRecording(reconfigure: true);
        Diagnostics.Log.Event("view.recording", ("on", false));
    }

    // The view keeps its drawing as it was before; the recording finishes on its own thread.
    private void LetGoOfRecording(bool reconfigure)
    {
        _recording?.Finish();
        _recording = null;
        _recordingMark?.Dispose();
        _recordingMark = null;
        if (!reconfigure || _thumbnail is null) return;
        _thumbnail.Record(null);
        _thumbnail.Configure(BackendPreferences());
    }

    private void FollowRecordingMark()
    {
        if (_recording is null || !Visible || _suspended || _cleaned)
        {
            _recordingMark?.Hide();
            return;
        }
        if (_recordingMark is { IsDisposed: true }) _recordingMark = null;
        if (_recordingMark is null)
        {
            _recordingMark = new PipRecordingMark(this);
            KeepPartOutOfScreenCapture(_recordingMark);
        }
        _recordingMark.Follow(Bounds);
    }

    // Said on the view when it is still there; a closed view's recording is saved all the same, and logged.
    private async void FollowRecording(ViewRecording recording)
    {
        try
        {
            RecordingResult result = await recording.Completion;
            await UiThread.Back(this);
            if (_cleaned || IsDisposed) return;
            LastRecording = result;
            if (_recording == recording) LetGoOfRecording(reconfigure: true);
            if (result.File is { } file) Say(T("pip.state.recordingSaved"), file);
            else if (result.Problem != 0) Problem?.Invoke(string.Format(T("errors.pip.recordFailed"), $"0x{result.Problem:X8}"));
            else Say(T("pip.state.nothingRecorded"));
            if (result.SoundRefused) Notice?.Invoke(T("pip.record.noSound"));
            Recorded?.Invoke(result);
        }
        catch (Exception failure) { Unexpected(failure); }
    }
}
