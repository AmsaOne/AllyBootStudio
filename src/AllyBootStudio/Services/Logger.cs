using System.Globalization;
using System.IO;
using System.Text;

namespace AllyBootStudio.Services;

public static class Logger
{
    private static readonly object _lock = new();
    private static string? _logFile;

    public static string LogDirectory { get; private set; } = "";
    public static string? LogFile => _logFile;

    public static void Initialize()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AllyBootStudio", "logs");
            Directory.CreateDirectory(dir);
            LogDirectory = dir;
            _logFile = Path.Combine(dir, $"app-{DateTime.Now:yyyy-MM-dd}.log");
            Info("=== AllyBootStudio session start ===");
        }
        catch
        {
            // If logger init itself fails there is nowhere to log it. Fall back to silent.
            _logFile = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static string WriteAuxiliaryLog(string prefix, string content)
    {
        try
        {
            if (string.IsNullOrEmpty(LogDirectory)) return "";
            var path = Path.Combine(LogDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, content);
            return path;
        }
        catch (Exception ex)
        {
            Error($"WriteAuxiliaryLog({prefix}) failed", ex);
            return "";
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        if (_logFile is null) return;
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(' ').Append(level).Append(' ').Append(message);
        if (ex is not null) sb.Append(Environment.NewLine).Append(ex);
        sb.Append(Environment.NewLine);
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_logFile, sb.ToString());
            }
        }
        catch
        {
            // Logger must never throw.
        }
    }
}
