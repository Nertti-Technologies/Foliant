namespace Foliant.Models;

internal static class ModelAssetFileName
{
    // A bare name, also by Windows rules: no separators or drive/stream colon, and no trailing dot or
    // space (Windows strips them, so "..." would name the cache directory itself).
    internal static bool IsPlainFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.IndexOfAny(['/', '\\', ':']) < 0
        && !fileName.EndsWith('.') && !fileName.EndsWith(' ')
        && Path.GetFileName(fileName) == fileName
        && !Path.IsPathRooted(fileName);
}
