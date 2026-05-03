using System.IO;
using System.Text.RegularExpressions;
using AllyBootStudio.Models;

namespace AllyBootStudio.Services;

public sealed class AcseFolderLocator
{
    // Known root folders Armoury Crate SE uses for boot animation user content.
    // The animation tree contains numbered folders (3-digit IDs like 359) each with one .mp4.
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

    private static readonly EnumerationOptions DirEnum = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
    };

    public string? DetectedRoot { get; private set; }

    public IReadOnlyList<AnimationSlot> Discover()
    {
        foreach (var root in CandidateRoots)
        {
            if (!SafeDirectoryExists(root)) continue;
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
        if (!SafeDirectoryExists(root))
        {
            DetectedRoot = null;
            return Array.Empty<AnimationSlot>();
        }
        var slots = EnumerateSlots(root).ToList();
        // Mirror Discover(): only set DetectedRoot when we actually found something usable.
        DetectedRoot = slots.Count == 0 ? null : root;
        return slots;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception ex)
        {
            Logger.Warn($"Directory.Exists('{path}') threw", ex);
            return false;
        }
    }

    private static IEnumerable<AnimationSlot> EnumerateSlots(string root)
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root, "*", DirEnum);
        }
        catch (Exception ex)
        {
            Logger.Error($"EnumerateDirectories('{root}') failed", ex);
            yield break;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (!SlotIdPattern.IsMatch(name)) continue;

            string? mp4 = null;
            try
            {
                mp4 = Directory.EnumerateFiles(dir, "*.mp4", DirEnum).FirstOrDefault();
            }
            catch (Exception ex)
            {
                Logger.Warn($"EnumerateFiles('{dir}') failed", ex);
            }
            if (mp4 is null) continue;

            var backupDir = Path.Combine(dir, "original");
            bool hasBackup = false;
            try
            {
                hasBackup = Directory.Exists(backupDir) &&
                            Directory.EnumerateFiles(backupDir, "*.mp4", DirEnum).Any();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Backup probe in '{backupDir}' failed", ex);
            }

            long size = 0;
            try { size = new FileInfo(mp4).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Logger.Warn($"FileInfo('{mp4}').Length failed", ex);
            }

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
