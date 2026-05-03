using System.IO;

namespace AllyBootStudio.Services;

public sealed class BootAnimationService
{
    public sealed record ReplaceResult(bool Success, string Message, string? BackupPath);

    public ReplaceResult Replace(string slotFolder, string sourceMp4)
    {
        if (!Directory.Exists(slotFolder))
            return new ReplaceResult(false, $"Slot folder not found: {slotFolder}", null);
        if (!File.Exists(sourceMp4))
            return new ReplaceResult(false, $"Source MP4 not found: {sourceMp4}", null);

        var existingMp4 = Directory.EnumerateFiles(slotFolder, "*.mp4", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (existingMp4 is null)
            return new ReplaceResult(false, "No existing MP4 found in slot folder.", null);

        var targetName = Path.GetFileName(existingMp4);
        var backupDir = Path.Combine(slotFolder, "original");
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, targetName);

        // Only back up if a backup does not already exist (preserve the genuine original).
        if (!File.Exists(backupPath))
        {
            try
            {
                File.Copy(existingMp4, backupPath, overwrite: false);
                MakeReadOnly(backupPath, true);
            }
            catch (Exception ex)
            {
                return new ReplaceResult(false, $"Backup failed: {ex.Message}", null);
            }
        }

        try
        {
            // Clear read-only on existing target file (in case prior writes locked it).
            MakeReadOnly(existingMp4, false);
            File.Copy(sourceMp4, existingMp4, overwrite: true);
            // Lock the new file read-only so Armoury Crate can't restore the original silently.
            MakeReadOnly(existingMp4, true);
        }
        catch (Exception ex)
        {
            return new ReplaceResult(false, $"Replace failed: {ex.Message}", backupPath);
        }

        return new ReplaceResult(true, "Boot animation replaced. Restart Armoury Crate SE for changes to take effect.", backupPath);
    }

    public ReplaceResult Restore(string slotFolder)
    {
        var backupDir = Path.Combine(slotFolder, "original");
        var backupMp4 = Directory.Exists(backupDir)
            ? Directory.EnumerateFiles(backupDir, "*.mp4").FirstOrDefault()
            : null;
        if (backupMp4 is null)
            return new ReplaceResult(false, "No backup MP4 found for this slot.", null);

        var targetName = Path.GetFileName(backupMp4);
        var target = Path.Combine(slotFolder, targetName);

        try
        {
            MakeReadOnly(target, false);
            MakeReadOnly(backupMp4, false);
            File.Copy(backupMp4, target, overwrite: true);
            MakeReadOnly(backupMp4, true);
        }
        catch (Exception ex)
        {
            return new ReplaceResult(false, $"Restore failed: {ex.Message}", backupMp4);
        }

        return new ReplaceResult(true, "Original animation restored.", backupMp4);
    }

    private static void MakeReadOnly(string path, bool readOnly)
    {
        if (!File.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if (readOnly) attrs |= FileAttributes.ReadOnly;
        else attrs &= ~FileAttributes.ReadOnly;
        File.SetAttributes(path, attrs);
    }
}
