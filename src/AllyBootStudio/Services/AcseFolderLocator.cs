using System.IO;
using System.Text.RegularExpressions;
using AllyBootStudio.Models;

namespace AllyBootStudio.Services;

public sealed class AcseFolderLocator
{
    // Known root folders Armoury Crate SE uses for boot animation user content.
    // The animation tree contains numbered folders (3-digit IDs like 359) each with one .mp4.
    // We search several known roots; the first that contains numbered folders wins.
    private static readonly string[] CandidateRoots = new[]
    {
        @"C:\ProgramData\ASUS\ARMOURY CRATE Service\ACSE\Animation",
        @"C:\ProgramData\ASUS\ARMOURY CRATE Service\Animation",
        @"C:\ProgramData\ASUS\ARMOURY CRATE Lite Service\Animation",
        @"C:\ProgramData\ASUS\ROG Live Service\Animation",
        @"C:\Program Files\ASUS\ARMOURY CRATE Service\Animation",
        @"C:\Program Files\ASUS\ARMOURY CRATE Lite Service\Animation",
        @"C:\Program Files (x86)\ASUS\ARMOURY CRATE Service\Animation",
    };

    private static readonly Regex SlotIdPattern = new(@"^\d{3}$", RegexOptions.Compiled);

    public string? DetectedRoot { get; private set; }

    public IReadOnlyList<AnimationSlot> Discover()
    {
        foreach (var root in CandidateRoots)
        {
            if (!Directory.Exists(root)) continue;
            var slots = EnumerateSlots(root).ToList();
            if (slots.Count == 0) continue;
            DetectedRoot = root;
            return slots;
        }

        DetectedRoot = null;
        return Array.Empty<AnimationSlot>();
    }

    public IReadOnlyList<AnimationSlot> DiscoverIn(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<AnimationSlot>();
        DetectedRoot = root;
        return EnumerateSlots(root).ToList();
    }

    private static IEnumerable<AnimationSlot> EnumerateSlots(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (!SlotIdPattern.IsMatch(name)) continue;

            var mp4 = Directory.EnumerateFiles(dir, "*.mp4", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (mp4 is null) continue;

            var backupDir = Path.Combine(dir, "original");
            var hasBackup = Directory.Exists(backupDir) &&
                            Directory.EnumerateFiles(backupDir, "*.mp4").Any();

            long size = 0;
            try { size = new FileInfo(mp4).Length; } catch { /* ignore */ }

            yield return new AnimationSlot(
                Id: name,
                FolderPath: dir,
                CurrentMp4Path: mp4,
                DisplayName: PrettyName(name),
                HasBackup: hasBackup,
                CurrentSizeBytes: size
            );
        }
    }

    private static string PrettyName(string id) => id switch
    {
        "352" => "Cult of the Lamb",
        "353" => "Starfield",
        "358" => "Robocop",
        "359" => "Default / ROG",
        _     => $"Slot {id}"
    };
}
