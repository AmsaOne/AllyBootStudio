namespace AllyBootStudio.Models;

public sealed record AnimationSlot(
    string Id,
    string FolderPath,
    string CurrentMp4Path,
    string DisplayName,
    bool HasBackup,
    long CurrentSizeBytes
);
