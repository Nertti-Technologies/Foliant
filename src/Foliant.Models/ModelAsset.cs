namespace Foliant.Models;

/// <summary>One downloadable model asset.</summary>
/// <param name="Id">Stable identifier (also the catalog lookup key).</param>
/// <param name="FileName">Local file name inside the cache/models directory.</param>
/// <param name="Url">Direct download URL (Hugging Face resolve link).</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file contents.</param>
/// <param name="SizeBytes">Expected size, for progress reporting and sanity checks.</param>
/// <exception cref="ArgumentNullException">The file name or SHA-256 is null.</exception>
/// <exception cref="ArgumentException">The file name is not a plain file name (it is joined onto the
/// cache directory), or the SHA-256 is not 64 lower-case hex characters (the form the cache compares
/// against).</exception>
public sealed record ModelAsset(
    string Id,
    string FileName,
    string Url,
    string Sha256,
    long SizeBytes)
{
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">The file name is not a plain file name.</exception>
    public string FileName { get; init => field = ValidateFileName(value, nameof(FileName)); } = ValidateFileName(FileName, nameof(FileName));

    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">The SHA-256 is not 64 lower-case hex characters.</exception>
    public string Sha256 { get; init => field = ValidateSha256(value, nameof(Sha256)); } = ValidateSha256(Sha256, nameof(Sha256));

    private static string ValidateFileName(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (!ModelAssetFileName.IsPlainFileName(value))
            throw new ArgumentException("Expected a plain file name.", paramName);
        return value;
    }

    private static string ValidateSha256(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("Expected 64 lower-case hexadecimal characters.", paramName);
        return value;
    }
}
