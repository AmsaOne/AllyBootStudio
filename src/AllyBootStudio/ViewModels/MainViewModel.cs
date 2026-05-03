using System.Collections.ObjectModel;
using System.ComponentModel;
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
                ValidateSource();
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

    private string _validationMessage = "Drop a .mp4 here or click Browse.";
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

    public RelayCommand RefreshSlotsCommand { get; }
    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand TranscodeCommand   { get; }
    public RelayCommand ApplyCommand       { get; }
    public RelayCommand RestoreCommand     { get; }
    public RelayCommand OpenSlotFolderCommand { get; }
    public RelayCommand PickRootCommand    { get; }

    public MainViewModel()
    {
        RefreshSlotsCommand    = new RelayCommand(() => RefreshSlots(),                () => !IsBusy);
        BrowseSourceCommand    = new RelayCommand(() => BrowseSource(),                () => !IsBusy);
        TranscodeCommand       = new RelayCommand(async () => await TranscodeAsync(),  () => !IsBusy && !string.IsNullOrEmpty(SourceMp4) && File.Exists(SourceMp4));
        ApplyCommand           = new RelayCommand(() => Apply(),                       () => !IsBusy && ValidationOk && SelectedSlot is not null);
        RestoreCommand         = new RelayCommand(() => Restore(),                     () => !IsBusy && SelectedSlot is { HasBackup: true });
        OpenSlotFolderCommand  = new RelayCommand(() => OpenSlotFolder(),              () => SelectedSlot is not null);
        PickRootCommand        = new RelayCommand(() => PickRoot(),                    () => !IsBusy);

        RefreshSlots();
    }

    public void RefreshSlots()
    {
        var prevId = SelectedSlot?.Id;
        Slots.Clear();
        var found = _locator.Discover();
        DetectedRoot = _locator.DetectedRoot ?? "(not detected — use Pick Folder to choose ACSE Animation root manually)";
        foreach (var s in found) Slots.Add(s);
        SelectedSlot = (prevId is not null ? Slots.FirstOrDefault(s => s.Id == prevId) : null)
                       ?? Slots.FirstOrDefault();
        StatusMessage = found.Count == 0
            ? "No animation slots detected."
            : $"Found {found.Count} slot{(found.Count == 1 ? "" : "s")}.";
    }

    private void PickRoot()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick the Armoury Crate SE Animation root folder",
        };
        if (dlg.ShowDialog() != true) return;
        var prevId = SelectedSlot?.Id;
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

    private void ValidateSource()
    {
        if (string.IsNullOrWhiteSpace(SourceMp4) || !File.Exists(SourceMp4))
        {
            ValidationMessage = "Drop a .mp4 here or click Browse.";
            ValidationOk = false;
            return;
        }

        var ext = Path.GetExtension(SourceMp4).ToLowerInvariant();
        if (ext != ".mp4")
        {
            ValidationMessage = $"Source is {ext} — click Transcode to convert to compatible MP4.";
            ValidationOk = false;
            return;
        }

        var result = _validator.Validate(SourceMp4);
        ValidationMessage = result.Message;
        ValidationOk = result.IsValid;
    }

    private async Task TranscodeAsync()
    {
        if (string.IsNullOrEmpty(SourceMp4) || !File.Exists(SourceMp4)) return;
        if (!_ffmpeg.IsAvailable())
        {
            StatusMessage = "ffmpeg not found on PATH. Install via: winget install Gyan.FFmpeg";
            return;
        }

        var dir = Path.GetDirectoryName(SourceMp4);
        if (string.IsNullOrEmpty(dir))
        {
            StatusMessage = "Source file has no parent directory — move it into a folder first.";
            return;
        }
        var name = Path.GetFileNameWithoutExtension(SourceMp4);
        var target = Path.Combine(dir, $"{name}.acse.mp4");

        IsBusy = true;
        StatusMessage = "Transcoding to H.264 MP4...";
        try
        {
            var (ok, _) = await _ffmpeg.TranscodeAsync(SourceMp4, target,
                progress: new Progress<string>(line => StatusMessage = TruncateLine(line)),
                cancellationToken: CancellationToken.None);
            if (ok)
            {
                SourceMp4 = target;
                StatusMessage = $"Transcoded to: {target}";
            }
            else
            {
                StatusMessage = "Transcode failed. See ffmpeg log.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Transcode error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void Apply()
    {
        if (SelectedSlot is null || string.IsNullOrEmpty(SourceMp4)) return;
        IsBusy = true;
        try
        {
            var result = _bootSvc.Replace(SelectedSlot.FolderPath, SourceMp4);
            StatusMessage = result.Message;
            if (result.Success) RefreshSlots();
        }
        finally { IsBusy = false; }
    }

    private void Restore()
    {
        if (SelectedSlot is null) return;
        IsBusy = true;
        try
        {
            var result = _bootSvc.Restore(SelectedSlot.FolderPath);
            StatusMessage = result.Message;
            if (result.Success) RefreshSlots();
        }
        finally { IsBusy = false; }
    }

    private void OpenSlotFolder()
    {
        if (SelectedSlot is null) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{SelectedSlot.FolderPath}\""); }
        catch (Exception ex) { StatusMessage = $"Open failed: {ex.Message}"; }
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

