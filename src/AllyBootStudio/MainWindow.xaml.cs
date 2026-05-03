using System.IO;
using System.Windows;
using AllyBootStudio.Services;
using AllyBootStudio.ViewModels;

namespace AllyBootStudio;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v"
    };

    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            // Only accept the drop if the cursor would actually do something useful — otherwise
            // we'd flash a "Copy OK" cursor for folders / .txt files and silently reject them on Drop.
            e.Effects = HasAcceptableVideo(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        }
        catch (Exception ex)
        {
            Logger.Warn("Window_DragOver threw", ex);
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            {
                _vm.StatusMessage = "That drop wasn't a file (try saving the attachment to disk first).";
                return;
            }

            var first = files.FirstOrDefault(f =>
                !string.IsNullOrWhiteSpace(f) &&
                File.Exists(f) &&
                VideoExtensions.Contains(Path.GetExtension(f)));

            if (first is null)
            {
                _vm.StatusMessage = "Drop a video file (.mp4, .mov, .mkv, .webm, .avi).";
                return;
            }

            _vm.DropFile(first);
        }
        catch (Exception ex)
        {
            Logger.Error("Window_Drop threw", ex);
            _vm.StatusMessage = $"Drop failed: {ex.Message}";
        }
    }

    private static bool HasAcceptableVideo(IDataObject? data)
    {
        if (data is null) return false;
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] files) return false;
        return files.Any(f =>
            !string.IsNullOrWhiteSpace(f) &&
            VideoExtensions.Contains(Path.GetExtension(f)));
    }
}
