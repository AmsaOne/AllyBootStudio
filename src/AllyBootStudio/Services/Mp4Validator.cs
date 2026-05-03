using System.Buffers.Binary;
using System.IO;

namespace AllyBootStudio.Services;

public sealed class Mp4Validator
{
    public sealed record ValidationResult(bool IsValid, string Message, string? DetectedCodec);

    // Walks the MP4 box hierarchy: ftyp -> moov -> trak -> mdia -> minf -> stbl -> stsd
    // The first sample-entry FourCC inside stsd identifies the codec exactly. This is
    // structurally correct (no false positives from metadata strings, no false negatives
    // when the moov box is past the first 4 MB of the file).
    public ValidationResult Validate(string path)
    {
        if (!File.Exists(path))
            return new ValidationResult(false, "File does not exist.", null);

        try
        {
            using var fs = File.OpenRead(path);

            if (!ReadFtyp(fs))
                return new ValidationResult(false, "Not an MP4 container (missing 'ftyp' box).", null);

            // Find the moov box at the top level (it can be before or after mdat).
            if (!TrySeekToBox(fs, "moov", fs.Length, out long moovEnd))
                return new ValidationResult(false, "MP4 has no 'moov' box (file truncated?).", null);

            // Iterate trak boxes inside moov.
            while (fs.Position < moovEnd)
            {
                if (!TryReadBoxHeader(fs, out var name, out long bodyStart, out long bodyEnd, moovEnd))
                    break;
                if (name != "trak") { fs.Position = bodyEnd; continue; }
                var codec = FindCodecInTrak(fs, bodyEnd);
                if (codec is not null) return Classify(codec);
                fs.Position = bodyEnd;
            }

            return new ValidationResult(false,
                "MP4 container OK, but no recognizable video track was found. Click Transcode to convert.",
                null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ValidationResult(false, $"Cannot read file (permission denied): {ex.Message}", null);
        }
        catch (IOException ex)
        {
            return new ValidationResult(false, $"Cannot read file: {ex.Message}", null);
        }
        catch (NotSupportedException ex)
        {
            return new ValidationResult(false, $"File source not supported: {ex.Message}", null);
        }
    }

    private static ValidationResult Classify(string codec) => codec switch
    {
        "avc1" or "avc3" => new ValidationResult(true, "MP4 + H.264 detected. Compatible.", "H.264"),
        "hvc1" or "hev1" => new ValidationResult(true, "MP4 + H.265 detected. Compatible.", "H.265"),
        _ => new ValidationResult(false,
            $"MP4 video codec is '{codec}', not H.264/H.265. Click Transcode to convert.",
            codec)
    };

    private static bool ReadFtyp(FileStream fs)
    {
        Span<byte> head = stackalloc byte[8];
        if (!ReadFull(fs, head)) return false;
        return head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p';
    }

    private static bool TrySeekToBox(FileStream fs, string targetName, long endLimit, out long bodyEnd)
    {
        fs.Position = 0;
        while (fs.Position < endLimit)
        {
            if (!TryReadBoxHeader(fs, out var name, out long bodyStart, out long localEnd, endLimit))
            {
                bodyEnd = 0;
                return false;
            }
            if (name == targetName)
            {
                bodyEnd = localEnd;
                return true;
            }
            fs.Position = localEnd;
        }
        bodyEnd = 0;
        return false;
    }

    private static bool TryReadBoxHeader(FileStream fs, out string name, out long bodyStart, out long bodyEnd, long containerEnd)
    {
        name = "";
        bodyStart = 0;
        bodyEnd = 0;
        Span<byte> hdr = stackalloc byte[8];
        if (!ReadFull(fs, hdr)) return false;

        long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
        name = System.Text.Encoding.ASCII.GetString(hdr[4..8]);
        long headerSize = 8;

        if (size == 1)
        {
            // 64-bit largesize follows.
            Span<byte> ext = stackalloc byte[8];
            if (!ReadFull(fs, ext)) return false;
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
            headerSize = 16;
        }
        else if (size == 0)
        {
            // Box extends to end of container.
            size = containerEnd - (fs.Position - headerSize);
        }

        if (size < headerSize) return false;
        bodyStart = fs.Position;
        bodyEnd = fs.Position - headerSize + size;
        if (bodyEnd > containerEnd) bodyEnd = containerEnd;
        return true;
    }

    // Inside a trak box, drill: trak -> mdia -> minf -> stbl -> stsd, then read the first sample
    // entry's 4-byte type code. That's the codec FourCC.
    private static string? FindCodecInTrak(FileStream fs, long trakEnd)
    {
        if (!DescendTo(fs, "mdia", trakEnd, out long mdiaEnd)) return null;
        if (!DescendTo(fs, "minf", mdiaEnd, out long minfEnd)) return null;
        if (!DescendTo(fs, "stbl", minfEnd, out long stblEnd)) return null;
        if (!DescendTo(fs, "stsd", stblEnd, out long stsdEnd)) return null;

        // stsd is a FullBox: 1 byte version + 3 bytes flags + 4 bytes entry_count, then entries.
        // Each entry begins with the same 8-byte size+type header.
        Span<byte> stsdHeader = stackalloc byte[8];
        if (!ReadFull(fs, stsdHeader)) return null;
        if (fs.Position >= stsdEnd) return null;

        Span<byte> entryHeader = stackalloc byte[8];
        if (!ReadFull(fs, entryHeader)) return null;
        var name = System.Text.Encoding.ASCII.GetString(entryHeader[4..8]);
        return name;
    }

    private static bool DescendTo(FileStream fs, string targetName, long containerEnd, out long bodyEnd)
    {
        while (fs.Position < containerEnd)
        {
            if (!TryReadBoxHeader(fs, out var name, out long bodyStart, out long localEnd, containerEnd))
            {
                bodyEnd = 0;
                return false;
            }
            if (name == targetName)
            {
                bodyEnd = localEnd;
                return true;
            }
            fs.Position = localEnd;
        }
        bodyEnd = 0;
        return false;
    }

    private static bool ReadFull(Stream s, Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf[total..]);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }
}
