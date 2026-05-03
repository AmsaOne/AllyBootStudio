using System.Diagnostics;
using System.IO;
using System.Text;

namespace AllyBootStudio.Services;

public sealed class FfmpegService
{
    private string? _resolvedFfmpegPath;
    private bool _resolved;
    private readonly object _resolveLock = new();

    public string? FfmpegPathOverride { get; set; }

    public bool IsAvailable() => !string.IsNullOrWhiteSpace(ResolveFfmpeg());

    public void InvalidateResolution()
    {
        lock (_resolveLock)
        {
            _resolved = false;
            _resolvedFfmpegPath = null;
        }
    }

    public string? ResolveFfmpeg()
    {
        lock (_resolveLock)
        {
            if (_resolved) return _resolvedFfmpegPath;
            _resolvedFfmpegPath = ResolveFfmpegUncached();
            _resolved = true;
            return _resolvedFfmpegPath;
        }
    }

    private string? ResolveFfmpegUncached()
    {
        if (!string.IsNullOrWhiteSpace(FfmpegPathOverride) && File.Exists(FfmpegPathOverride))
            return FfmpegPathOverride;

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate;
                try
                {
                    candidate = Path.Combine(dir, "ffmpeg.exe");
                }
                catch (ArgumentException ex)
                {
                    Logger.Warn($"Invalid PATH entry '{dir}'", ex);
                    continue;
                }
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warn($"Could not stat '{candidate}'", ex);
                }
            }
        }

        string[] common = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe"),
            Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\scoop\shims\ffmpeg.exe"),
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
        };
        foreach (var c in common)
        {
            try { if (File.Exists(c)) return c; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Could not stat '{c}'", ex);
            }
        }
        return null;
    }

    public sealed record TranscodeOutcome(bool Success, string Log, string? LogFilePath, Exception? Exception = null);

    public async Task<TranscodeOutcome> TranscodeAsync(
        string sourcePath,
        string targetPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
            return new TranscodeOutcome(false, "ffmpeg not found. Install via: winget install Gyan.FFmpeg", null);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // ArgumentList encodes each item as a separate argv entry, so paths with quotes
        // or spaces cannot inject extra ffmpeg flags.
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("medium");
        psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("20");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale='min(1920,iw)':'-2'");
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("160k");
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(targetPath);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var log = new StringBuilder();
        var lockObj = new object();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lockObj) log.AppendLine(e.Data);
            progress?.Report(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lockObj) log.AppendLine(e.Data);
            progress?.Report(e.Data);
        };

        try
        {
            proc.Start(); // throws on failure; no pointless if (!Start()) check.
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start ffmpeg at '{ffmpeg}'", ex);
            return new TranscodeOutcome(false, $"Failed to start ffmpeg: {ex.Message}", null, ex);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch (Exception killEx)
            {
                Logger.Warn("Could not kill ffmpeg on cancellation", killEx);
            }
            // Drain any remaining output.
            try { proc.WaitForExit(2000); } catch { /* best effort */ }
            // Delete the partial output so a follow-up attempt isn't blocked by a locked file.
            try
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);
            }
            catch (Exception delEx) { Logger.Warn($"Could not delete partial output '{targetPath}'", delEx); }

            string cancelLog;
            lock (lockObj) cancelLog = log.ToString();
            var cancelLogPath = Logger.WriteAuxiliaryLog("transcode-cancelled", cancelLog);
            throw; // let caller distinguish cancellation from failure.
        }

        // Flush async stream reader buffers — WaitForExitAsync does not.
        try { proc.WaitForExit(); } catch { /* already exited */ }

        string fullLog;
        lock (lockObj) fullLog = log.ToString();
        bool ok = proc.ExitCode == 0;
        var logPath = Logger.WriteAuxiliaryLog(ok ? "transcode-ok" : "transcode-failed", fullLog);
        return new TranscodeOutcome(ok, fullLog, logPath);
    }
}
