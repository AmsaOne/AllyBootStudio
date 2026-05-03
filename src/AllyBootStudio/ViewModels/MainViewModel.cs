using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using AllyBootStudio.Models;
using AllyBootStudio.Services;
using Microsoft.Win32;

namespace AllyBootStudio.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AcseFolderLocator _locator = new();
    private readonly BootAnimationService _bootSvc = new();
    private readonly Mp4Validator _validator = new();
    private readonly FfmpegService _ffmpeg = new();
    private CancellationTokenSource? _transcodeCts;

    public ObservableCollection<AnimationSlot> Slots { get; } = new();

    private AnimationSlot? _selectedSlot;
    public AnimationSlot? SelectedSlot
    {
        get => _selectedSlot;
        set { if (Set(ref _selectedSlot, value)) RefreshCommands(); }
    }

    private string? _sourceMp4;
    public string? SourceMp4
    {
        get => _sourceMp4;
        set
        {
            if (Set(ref _sourceMp4, value))
            {
                _ = ValidateSourceAsync();
                RefreshCommands();
            }
        }
    }

    private string _detectedRoot = "(not detected — install Armoury Crate SE on this device)";
    public string DetectedRoot
    {
        get => _detectedRoot;
        set => Set(ref _detectedRoot, value);
    }

    private string _validationMessage = "Drop a video here or click Browse.";
    public string ValidationMessage
    {
        get => _validationMessage;
        set => Set(ref _validationMessage, value);
    }

    private bool _validationOk;
    public bool ValidationOk
    {
        get => _validationOk;
        set { if (Set(ref _validationOk, value)) RefreshCommands(); }
    }

    private string _statusMessage = "Ready.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) RefreshCommands(); }
    }

    private bool _ffmpegAvailable;
    public bool FfmpegAvailable
    {
        get => _ffmpegAvailable;
        private set { if (Set(ref _ffmpegAvailable, value)) RefreshCommands(); }
    }

    public RelayCommand RefreshSlotsCommand { get; }
    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand TranscodeCommand   { get; }
    public RelayCommand ApplyCommand       { get; }
    public RelayCommand RestoreCommand     { get; }
    public RelayCommand OpenSlotFolderCommand { get; }
    public RelayCommand PickRootCommand    { get; }
    public RelayCommand CancelCommand      { get; }

    public MainViewModel()
    {
        RefreshSlotsCommand    = new RelayCommand(() => RefreshSlots(),                 () => !IsBusy);
        BrowseSourceCommand    = new RelayCommand(() => BrowseSource(),                 () => !IsBusy);
        TranscodeCommand       = new RelayCommand(TranscodeAsync,                       () => !IsBusy && FfmpegAvailable && !string.IsNullOrEmpty(SourceMp4) && SafeFileExists(SourceMp4));
        ApplyCommand           = new RelayCommand(ApplyAsync,                           () => !IsBusy && ValidationOk && SelectedSlot is not null);
        RestoreCommand         = new RelayCommand(RestoreAsync,                         () => !IsBusy && SelectedSlot is { HasBackup: true });
        OpenSlotFolderCommand  = new RelayCommand(() => OpenSlotFolder(),               () => SelectedSlot is not null);
        PickRootCommand        = new RelayCommand(() => PickRoot(),                     () => !IsBusy);
        CancelCommand          = new RelayCommand(() => CancelTranscode(),              () => IsBusy && _transcodeCts is not null);

        FfmpegAvailable = _ffmpeg.IsAvailable();
        RefreshSlots();
    }

    public void RefreshSlots()
    {
        var prevId = SelectedSlot?.Id;
        Slots.Clear();
        try
        {
            var found = _locator.Discover();
            DetectedRoot = _locator.DetectedRoot ?? "(not detected — use Pick Folder to choose ACSE Animation root manually)";
            foreach (var s in found) Slots.Add(s);
            SelectedSlot = (prevId is not null ? Slots.FirstOrDefault(s => s.Id == prevId) : null)
                           ?? Slots.FirstOrDefault();
            StatusMessage = found.Count == 0
                ? "No animation slots detected."
                : $"Found {found.Count} slot{(found.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            Logger.Error("RefreshSlots failed", ex);
            StatusMessage = $"Slot scan failed: {ex.Message}";
        }
    }

    private void PickRoot()
    {
        var dlg = new OpenFolderDialog { Title = "Pick the Armoury Crate SE Animation root folder" };
        if (dlg.ShowDialog() != true) return;
        var prevId = SelectedSlot?.Id;
        try
        {
            var slots = _locator.DiscoverIn(dlg.FolderName);
            Slots.Clear();
            DetectedRoot = _locator.DetectedRoot ?? dlg.FolderName;
            foreach (var s in slots) Slots.Add(s);
            SelectedSlot = (prevId is not null ? Slots.FirstOrDefault(s => s.Id == prevId) : null)
                           ?? Slots.FirstOrDefault();
            StatusMessage = slots.Count == 0
                ? "No 3-digit slot folders found here."
                : $"Found {slots.Count} slot{(slots.Count == 1 ? "" : "s")} in chosen folder.";
        }
        catch (Exception ex)
        {
            Logger.Error("PickRoot scan failed", ex);
            StatusMessage = $"Folder scan failed: {ex.Message}";
        }
    }

    private void BrowseSource()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a video for the boot animation",
            Filter = "Video files|*.mp4;*.mov;*.mkv;*.webm;*.avi|All files|*.*",
        };
        if (dlg.ShowDialog() == true) SourceMp4 = dlg.FileName;
    }

    public void DropFile(string path)
    {
        SourceMp4 = path;
    }

    private async Task ValidateSourceAsync()
    {
        var path = SourceMp4;
        if (string.IsNullOrWhiteSpace(path) || !SafeFileExists(path))
        {
            ValidationMessage = "Drop a video here or click Browse.";
            ValidationOk = false;
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        // For non-mp4 containers we don't even attempt structural validation — the validator
        // is MP4-specific. The user can transcode.
        if (ext != ".mp4")
        {
            ValidationMessage = $"Source is {ext} — click Transcode to convert to compatible MP4.";
            ValidationOk = false;
            return;
        }

        ValidationMessage = "Validating...";
        try
        {
            var result = await Task.Run(() => _validator.Validate(path)).ConfigureAwait(true);
            // If the user changed the source while we were validating, drop this result.
            if (!string.Equals(path, SourceMp4, StringComparison.Ordinal)) return;
            ValidationMessage = result.Message;
            ValidationOk = result.IsValid;
        }
        catch (Exception ex)
        {
            Logger.Error("ValidateSourceAsync failed", ex);
            ValidationMessage = $"Validation error: {ex.Message}";
            ValidationOk = false;
        }
    }

    private async Task TranscodeAsync()
    {
        var src = SourceMp4;
        if (string.IsNullOrEmpty(src) || !SafeFileExists(src)) return;

        // Re-probe ffmpeg in case the user installed it after launch.
        _ffmpeg.InvalidateResolution();
        FfmpegAvailable = _ffmpeg.IsAvailable();
        if (!FfmpegAvailable)
        {
            StatusMessage = "ffmpeg not found on PATH. Install via: winget install Gyan.FFmpeg";
            return;
        }

        var dir = Path.GetDirectoryName(src);
        if (string.IsNullOrEmpty(dir))
        {
            StatusMessage = "Source file has no parent directory — move it into a folder first.";
            return;
        }
        var name = Path.GetFileNameWithoutExtension(src);
        var target = Path.Combine(dir, $"{name}.acse.mp4");

        _transcodeCts = new CancellationTokenSource();
        IsBusy = true;
        StatusMessage = "Transcoding to H.264 MP4...";
        try
        {
            var outcome = await _ffmpeg.TranscodeAsync(src, target,
                progress: new Progress<string>(line => StatusMessage = TruncateLine(line)),
                cancellationToken: _transcodeCts.Token).ConfigureAwait(true);

            if (outcome.Success)
            {
                SourceMp4 = target;
                // SourceMp4 setter triggers async validation; ValidationMessage will update.
                StatusMessage = outcome.LogFilePath is null
                    ? $"Transcoded to: {target}"
                    : $"Transcoded to: {target}  (log: {outcome.LogFilePath})";
            }
            else
            {
                Logger.Warn($"Transcode failed (log: {outcome.LogFilePath ?? "unsaved"})");
                StatusMessage = outcome.LogFilePath is null
                    ? "Transcode failed."
                    : $"Transcode failed. See log: {outcome.LogFilePath}";
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Transcode cancelled by user");
            StatusMessage = "Transcode cancelled.";
        }
        catch (Exception ex)
        {
            Logger.Error("Transcode threw", ex);
            StatusMessage = $"Transcode error: {ex.Message}";
        }
        finally
        {
            _transcodeCts?.Dispose();
            _transcodeCts = null;
            IsBusy = false;
        }
    }

    private void CancelTranscode()
    {
        try { _transcodeCts?.Cancel(); }
        catch (Exception ex) { Logger.Warn("Cancel failed", ex); }
    }

    private async Task ApplyAsync()
    {
        var slot = SelectedSlot;
        var src = SourceMp4;
        if (slot is null || string.IsNullOrEmpty(src)) return;
        IsBusy = true;
        try
        {
            // File copy of a 50–200 MB video must run off the UI thread.
            var result = await Task.Run(() => _bootSvc.Replace(slot.FolderPath, src)).ConfigureAwait(true);
            StatusMessage = result.Message;
            if (!result.Success && result.BackupPath is not null)
                StatusMessage += $"  (Backup at: {result.BackupPath})";
            // Always refresh — on partial failure the on-disk state may have changed.
            RefreshSlots();
        }
        catch (Exception ex)
        {
            Logger.Error("ApplyAsync threw", ex);
            StatusMessage = $"Apply error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task RestoreAsync()
    {
        var slot = SelectedSlot;
        if (slot is null) return;
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _bootSvc.Restore(slot.FolderPath)).ConfigureAwait(true);
            StatusMessage = result.Message;
            RefreshSlots();
        }
        catch (Exception ex)
        {
            Logger.Error("RestoreAsync threw", ex);
            StatusMessage = $"Restore error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void OpenSlotFolder()
    {
        var slot = SelectedSlot;
        if (slot is null) return;
        try
        {
            // FileName + ArgumentList avoids the manual quote-wrapping that would break on
            // paths containing a quote character.
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(slot.FolderPath);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Error("OpenSlotFolder failed", ex);
            StatusMessage = $"Open failed: {ex.Message}";
        }
    }

    private static bool SafeFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static string TruncateLine(string s) =>
        s.Length > 140 ? s[..137] + "..." : s;

    private void RefreshCommands()
    {
        RefreshSlotsCommand.RaiseCanExecuteChanged();
        BrowseSourceCommand.RaiseCanExecuteChanged();
        TranscodeCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        OpenSlotFolderCommand.RaiseCanExecuteChanged();
        PickRootCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        return true;
    }
}
