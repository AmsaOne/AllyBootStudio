using System.IO;
using System.Security.Cryptography;

namespace AllyBootStudio.Services;

public sealed class BootAnimationService
{
    public enum FailureStep
    {
        None,
        ValidateInputs,
        EnumerateSlot,
        SameFileGuard,
        BackupCopy,
        BackupVerify,
        ClearReadOnlyOnTarget,
        WriteTempCopy,
        AtomicReplace,
        SetReadOnlyOnTarget,
        ReadBackup,
        WriteRestore,
        ReLockBackup,
    }

    public sealed record OperationResult(
        bool Success,
        string Message,
        string? BackupPath,
        FailureStep FailedAt = FailureStep.None,
        Exception? Exception = null);

    public OperationResult Replace(string slotFolder, string sourceMp4)
    {
        // 1. Validate inputs.
        if (!Directory.Exists(slotFolder))
            return Fail(FailureStep.ValidateInputs, $"Slot folder not found: {slotFolder}", null, null);
        if (!File.Exists(sourceMp4))
            return Fail(FailureStep.ValidateInputs, $"Source MP4 not found: {sourceMp4}", null, null);

        // 2. Find the existing slot file.
        string? existingMp4;
        try
        {
            existingMp4 = Directory.EnumerateFiles(slotFolder, "*.mp4", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return Fail(FailureStep.EnumerateSlot, $"Could not list slot folder: {ex.Message}", null, ex);
        }
        if (existingMp4 is null)
            return Fail(FailureStep.EnumerateSlot, "No existing MP4 found in slot folder.", null, null);

        // 3. Reject same-file copy. File.Copy(self → self) truncates to zero bytes on Windows.
        if (PathsEqual(sourceMp4, existingMp4))
            return Fail(FailureStep.SameFileGuard,
                "Source file is the slot file itself. Pick a different file.", null, null);

        var targetName = Path.GetFileName(existingMp4);
        var backupDir = Path.Combine(slotFolder, "original");
        string backupPath = Path.Combine(backupDir, targetName);

        // 4. Create backup of the genuine original. Skipped on subsequent runs so we never
        //    overwrite the first backup.
        try
        {
            Directory.CreateDirectory(backupDir);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return Fail(FailureStep.BackupCopy, $"Could not create backup folder: {ex.Message}", null, ex);
        }

        if (!File.Exists(backupPath))
        {
            try
            {
                File.Copy(existingMp4, backupPath, overwrite: false);
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                return Fail(FailureStep.BackupCopy, $"Backup failed: {ex.Message}", null, ex);
            }

            // Verify the backup matches the source byte-for-byte before locking it.
            try
            {
                if (!FilesAreIdentical(existingMp4, backupPath))
                {
                    SafeDelete(backupPath);
                    return Fail(FailureStep.BackupVerify,
                        "Backup integrity check failed (size/hash mismatch). Backup deleted.", null, null);
                }
            }
            catch (Exception ex)
            {
                SafeDelete(backupPath);
                return Fail(FailureStep.BackupVerify, $"Backup verification failed: {ex.Message}", null, ex);
            }

            // Backup is good. Lock it read-only.
            TryMakeReadOnly(backupPath, true);
        }

        // 5. Stage the new MP4 to a temp file alongside the slot, then atomic-replace.
        //    File.Replace is atomic on NTFS and survives partial writes (the destination keeps
        //    its old contents if the call fails before the rename).
        var tempPath = existingMp4 + ".tmp";
        SafeDelete(tempPath);

        try
        {
            File.Copy(sourceMp4, tempPath, overwrite: true);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            SafeDelete(tempPath);
            return Fail(FailureStep.WriteTempCopy, $"Write failed: {ex.Message}", backupPath, ex);
        }

        // Verify the staged copy looks reasonable (size > 0 and matches source).
        try
        {
            var srcLen = new FileInfo(sourceMp4).Length;
            var dstLen = new FileInfo(tempPath).Length;
            if (srcLen != dstLen)
            {
                SafeDelete(tempPath);
                return Fail(FailureStep.WriteTempCopy,
                    $"Staged copy size mismatch ({dstLen} vs {srcLen} bytes).", backupPath, null);
            }
        }
        catch (Exception ex)
        {
            SafeDelete(tempPath);
            return Fail(FailureStep.WriteTempCopy, $"Staged copy verification failed: {ex.Message}", backupPath, ex);
        }

        // 6. Clear read-only on the existing target then atomic-replace it with the staged file.
        try
        {
            TryMakeReadOnly(existingMp4, false);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            SafeDelete(tempPath);
            return Fail(FailureStep.ClearReadOnlyOnTarget,
                $"Could not clear read-only on target: {ex.Message}", backupPath, ex);
        }

        try
        {
            // File.Replace requires destination to exist; ours always does (we located it above).
            File.Replace(tempPath, existingMp4, destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            SafeDelete(tempPath);
            return Fail(FailureStep.AtomicReplace,
                $"Atomic replace failed: {ex.Message}", backupPath, ex);
        }

        // 7. Re-lock the new file read-only so ACSE can't silently restore the original.
        try
        {
            TryMakeReadOnly(existingMp4, true);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // The file is in place but unprotected. Surface this clearly rather than claiming success.
            return new OperationResult(
                Success: true,
                Message: "Replaced, but could not set read-only. ACSE may overwrite the file.",
                BackupPath: backupPath,
                FailedAt: FailureStep.SetReadOnlyOnTarget,
                Exception: ex);
        }

        return new OperationResult(
            Success: true,
            Message: "Boot animation replaced. Restart Armoury Crate SE for changes to take effect.",
            BackupPath: backupPath);
    }

    public OperationResult Restore(string slotFolder)
    {
        var backupDir = Path.Combine(slotFolder, "original");
        if (!Directory.Exists(backupDir))
            return Fail(FailureStep.ReadBackup, "No backup folder found for this slot.", null, null);

        // Look for a backup whose filename matches the current slot mp4. If the slot is empty
        // or no name match, fall back to the first .mp4 in original/, but ensure it gets restored
        // under a sensible name.
        string? slotMp4;
        try
        {
            slotMp4 = Directory.EnumerateFiles(slotFolder, "*.mp4", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return Fail(FailureStep.EnumerateSlot, $"Could not list slot folder: {ex.Message}", null, ex);
        }

        string? backupMp4;
        try
        {
            var backups = Directory.EnumerateFiles(backupDir, "*.mp4").ToList();
            if (backups.Count == 0)
                return Fail(FailureStep.ReadBackup, "No backup MP4 found for this slot.", null, null);

            backupMp4 = slotMp4 is not null
                ? backups.FirstOrDefault(b => string.Equals(Path.GetFileName(b),
                                                            Path.GetFileName(slotMp4),
                                                            StringComparison.OrdinalIgnoreCase))
                  ?? backups[0]
                : backups[0];
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return Fail(FailureStep.ReadBackup, $"Could not list backup folder: {ex.Message}", null, ex);
        }

        // Target filename: use the slot's current mp4 name if present, otherwise the backup's name.
        var targetName = slotMp4 is not null ? Path.GetFileName(slotMp4) : Path.GetFileName(backupMp4);
        var target = Path.Combine(slotFolder, targetName);
        var tempPath = target + ".tmp";

        try
        {
            TryMakeReadOnly(target, false);
            TryMakeReadOnly(backupMp4, false);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Ensure backup ends up read-only no matter what.
            TryMakeReadOnly(backupMp4, true);
            return Fail(FailureStep.ClearReadOnlyOnTarget,
                $"Could not clear read-only attribute: {ex.Message}", backupMp4, ex);
        }

        SafeDelete(tempPath);
        try
        {
            File.Copy(backupMp4, tempPath, overwrite: true);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            SafeDelete(tempPath);
            TryMakeReadOnly(backupMp4, true);
            return Fail(FailureStep.WriteRestore, $"Restore copy failed: {ex.Message}", backupMp4, ex);
        }

        try
        {
            if (File.Exists(target))
            {
                File.Replace(tempPath, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, target);
            }
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            SafeDelete(tempPath);
            TryMakeReadOnly(backupMp4, true);
            return Fail(FailureStep.AtomicReplace, $"Restore swap failed: {ex.Message}", backupMp4, ex);
        }

        // Always re-lock the backup, regardless of what happened above.
        try { TryMakeReadOnly(backupMp4, true); }
        catch (Exception ex)
        {
            Logger.Warn($"Could not re-lock backup '{backupMp4}'", ex);
        }

        // Lock the restored file too — the read-only flag is the whole defense against ACSE.
        try { TryMakeReadOnly(target, true); }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return new OperationResult(
                Success: true,
                Message: "Original animation restored, but could not re-lock the file.",
                BackupPath: backupMp4,
                FailedAt: FailureStep.ReLockBackup,
                Exception: ex);
        }

        return new OperationResult(
            Success: true,
            Message: "Original animation restored.",
            BackupPath: backupMp4);
    }

    private static OperationResult Fail(FailureStep step, string message, string? backup, Exception? ex)
    {
        Logger.Error($"BootAnimationService [{step}]: {message}", ex);
        return new OperationResult(false, message, backup, step, ex);
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool FilesAreIdentical(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;

        // Hash compare for cheap byte-equality without holding both files in memory.
        using var sha = SHA256.Create();
        byte[] hashA, hashB;
        using (var sa = File.OpenRead(a)) hashA = sha.ComputeHash(sa);
        using (var sb = File.OpenRead(b)) hashB = sha.ComputeHash(sb);
        return hashA.AsSpan().SequenceEqual(hashB);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                TryMakeReadOnly(path, false);
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"SafeDelete('{path}') failed", ex);
        }
    }

    private static void TryMakeReadOnly(string path, bool readOnly)
    {
        if (!File.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if (readOnly) attrs |= FileAttributes.ReadOnly;
        else attrs &= ~FileAttributes.ReadOnly;
        File.SetAttributes(path, attrs);
    }

    private static bool IsFileSystemError(Exception ex) =>
        ex is IOException
           or UnauthorizedAccessException
           or System.Security.SecurityException
           or DirectoryNotFoundException
           or PathTooLongException
           or NotSupportedException;
}
