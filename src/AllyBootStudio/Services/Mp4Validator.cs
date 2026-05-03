using System.IO;

namespace AllyBootStudio.Services;

public sealed class Mp4Validator
{
    public sealed record ValidationResult(bool IsValid, string Message, string? DetectedCodec);

    // Lightweight check: confirm the file is an MP4 (ftyp box) and look for h264/h265 codec hints
    // in the moov box. This is intentionally heuristic — for the strict guarantee, the user can
    // run the FfmpegService probe.
    public ValidationResult Validate(string path)
    {
        if (!File.Exists(path))
            return new ValidationResult(false, "File does not exist.", null);

        try
        {
            using var fs = File.OpenRead(path);
            // First 12 bytes: <size:4><'ftyp'><major-brand:4>
            Span<byte> head = stackalloc byte[12];
            int read = fs.Read(head);
            if (read < 12) return new ValidationResult(false, "File too small to be an MP4.", null);
            if (head[4] != 'f' || head[5] != 't' || head[6] != 'y' || head[7] != 'p')
                return new ValidationResult(false, "Not an MP4 container (missing 'ftyp' box).", null);

            // Read up to 4 MB scanning for 'avc1' (h264) or 'hvc1'/'hev1' (h265) FourCC.
            fs.Position = 0;
            byte[] buf = new byte[Math.Min(fs.Length, 4 * 1024 * 1024)];
            int n = fs.Read(buf, 0, buf.Length);
            string codec = ScanCodec(buf, n);
            if (codec is "avc1" or "h264")
                return new ValidationResult(true, "MP4 + H.264 detected. Compatible.", "H.264");
            if (codec is "hvc1" or "hev1")
                return new ValidationResult(true, "MP4 + H.265 detected. Compatible.", "H.265");

            return new ValidationResult(false,
                "MP4 container OK, but codec is not H.264/H.265. Click Transcode to convert.",
                null);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"Read failed: {ex.Message}", null);
        }
    }

    private static string ScanCodec(byte[] buf, int n)
    {
        ReadOnlySpan<byte> avc1 = new byte[] { (byte)'a', (byte)'v', (byte)'c', (byte)'1' };
        ReadOnlySpan<byte> hvc1 = new byte[] { (byte)'h', (byte)'v', (byte)'c', (byte)'1' };
        ReadOnlySpan<byte> hev1 = new byte[] { (byte)'h', (byte)'e', (byte)'v', (byte)'1' };
        ReadOnlySpan<byte> span = buf.AsSpan(0, n);
        if (span.IndexOf(avc1) >= 0) return "avc1";
        if (span.IndexOf(hvc1) >= 0) return "hvc1";
        if (span.IndexOf(hev1) >= 0) return "hev1";
        return "";
    }
}
