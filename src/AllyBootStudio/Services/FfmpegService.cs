using System.Diagnostics;
using System.IO;

namespace AllyBootStudio.Services;

public sealed class FfmpegService
{
    public string? FfmpegPath { get; set; }

    public bool IsAvailable() => !string.IsNullOrWhiteSpace(ResolveFfmpeg());

    public string? ResolveFfmpeg()
    {
        if (!string.IsNullOrWhiteSpace(FfmpegPath) && File.Exists(FfmpegPath))
            return FfmpegPath;

        // Probe PATH.
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* skip */ }
            }
        }

        // Common winget / scoop / chocolatey install locations.
        string[] common = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe"),
            Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\scoop\shims\ffmpeg.exe"),
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
        };
        foreach (var c in common) if (File.Exists(c)) return c;
        return null;
    }

    public async Task<(bool ok, string log)> TranscodeAsync(
        string sourcePath,
        string targetPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return (false, "ffmpeg not found. Install ffmpeg or set the path in settings.");

        // Re-encode video to H.264 yuv420p, copy or transcode audio to AAC, faststart for quick mp4 demux.
        // Cap to 1080p so the file fits the boot animation footprint.
        var args = $"-y -i \"{sourcePath}\" " +
                   "-c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p " +
                   "-vf \"scale='min(1920,iw)':'-2'\" " +
                   "-c:a aac -b:a 160k -movflags +faststart " +
                   $"\"{targetPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var log = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { log.AppendLine(e.Data); progress?.Report(e.Data); } };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) { log.AppendLine(e.Data); progress?.Report(e.Data); } };

        if (!proc.Start()) return (false, "Failed to start ffmpeg process.");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (proc.ExitCode == 0, log.ToString());
    }
}
