using System.Diagnostics.CodeAnalysis;

namespace Foliant.DocumentExtraction;

/// <summary>Configures a <see cref="FoliantDocumentExtractionClient"/>.</summary>
[Experimental(FoliantDocumentExtractionClient.DiagnosticId)]
public sealed record FoliantDocumentExtractionClientOptions
{
    /// <summary>
    /// Processing options for every extraction; <see cref="ProcessingOptions.Default"/> when null.
    /// A single call can override them through
    /// <see cref="FoliantDocumentExtractionClient.ProcessingOptionsKey"/>.
    /// </summary>
    public ProcessingOptions? Processing { get; init; }

    /// <summary>
    /// Largest input accepted, in bytes, from 1 to <see cref="Array.MaxLength"/>. Longer streams are
    /// rejected with <see cref="InvalidDataException"/> before any processing (seekable streams before
    /// any read). Worst-case memory while buffering: one copy for a seekable stream; about three times
    /// this value for a non-seekable one (the growing buffer and the final array). Processing time
    /// scales with the page count, which this limit does not bound. Default 512 MiB, an estimate: about
    /// 16 times the largest of 10 real-world books sampled in testing (31 MB), chosen to stop
    /// unbounded buffering, not as a performance target.
    /// </summary>
    public long MaxInputBytes { get; init; } = 512L * 1024 * 1024;
}
