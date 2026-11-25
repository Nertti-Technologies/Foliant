using System.Diagnostics.CodeAnalysis;
using Foliant.Pipeline;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace Foliant.DocumentExtraction;

/// <summary>
/// A <see cref="DocumentExtractionClient"/> that extracts PDFs locally with Foliant: layout
/// detection, OCR where needed, table structure and reading order, with no network calls at
/// extraction time.
/// </summary>
/// <remarks>
/// <para>
/// The result has one section per page, in Foliant's reading order, with coordinates in points from
/// a top-left origin. Foliant's review signals are in <see cref="ExtractionDocument.Metadata"/>
/// (<c>foliant.pagesNeedingReview</c>, <c>foliant.sensitivityMarkedPages</c>,
/// <c>foliant.pagesWithNotice</c>, <c>foliant.coverageHolds</c>, <c>foliant.recallPercent</c>), on
/// each page section's payload, and in full as the <see cref="DocumentResult"/> in
/// <see cref="ExtractionDocument.RawRepresentation"/>. Callers that index or embed the text should
/// check <c>foliant.pagesNeedingReview</c>, <c>foliant.sensitivityMarkedPages</c> and
/// <c>foliant.pagesWithNotice</c> first: those pages are extracted, not withheld, and a page with a
/// notice may be empty for a reason the notice explains.
/// </para>
/// <para>Thread safety is that of the wrapped processor.</para>
/// </remarks>
[Experimental(DiagnosticId)]
public sealed class FoliantDocumentExtractionClient : DocumentExtractionClient
{
    /// <summary>Diagnostic ID for this experimental API.</summary>
    public const string DiagnosticId = "FOLIANT0001";

    /// <summary>
    /// Key in <see cref="DocumentExtractionOptions.AdditionalProperties"/> for a per-call
    /// <see cref="ProcessingOptions"/> value. Any other key is rejected.
    /// </summary>
    public const string ProcessingOptionsKey = "foliant.processingOptions";

    private const string PdfMediaType = "application/pdf";
    private const int CopyBufferBytes = 81_920; // Stream.CopyTo's default buffer size

    private readonly IDocumentProcessor _processor;
    private readonly DocumentProcessor? _ownedProcessor;
    private readonly FoliantDocumentExtractionClientOptions _options;
    private bool _disposed;

    /// <summary>Creates a client over a processor the caller owns and disposes.</summary>
    public FoliantDocumentExtractionClient(
        IDocumentProcessor processor, FoliantDocumentExtractionClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processor = processor;
        _options = Validate(options);
    }

    private FoliantDocumentExtractionClient(DocumentProcessor owned, FoliantDocumentExtractionClientOptions? options)
        : this((IDocumentProcessor)owned, options)
    {
        _ownedProcessor = owned;
    }

    /// <summary>
    /// Creates a client with Foliant's default pipeline, which this client owns and disposes. Missing
    /// models are downloaded once into the local cache (SHA-256 verified); extraction itself makes no
    /// network calls.
    /// </summary>
    public static async Task<FoliantDocumentExtractionClient> CreateDefaultAsync(
        FoliantDocumentExtractionClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options = Validate(options); // before the model download, so bad options fail fast
        var processor = await FoliantProcessor.CreateDefaultAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new FoliantDocumentExtractionClient(processor, options);
        }
        catch
        {
            processor.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentException">The identifier is empty, or <paramref name="options"/> sets
    /// anything other than <see cref="ProcessingOptionsKey"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="mediaType"/> is not application/pdf.</exception>
    /// <exception cref="InvalidDataException">The stream is longer than
    /// <see cref="FoliantDocumentExtractionClientOptions.MaxInputBytes"/>, or the processor returned a
    /// page with a non-positive DPI.</exception>
    public override async Task<ExtractionDocument> ExtractAsync(
        Stream document, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(mediaType);
        if (!IsPdf(mediaType))
        {
            throw new NotSupportedException("Only application/pdf is supported.");
        }

        var processing = ReadOptions(options) ?? _options.Processing;
        var pdf = await ReadBoundedAsync(document, _options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
        var result = await _processor.ProcessAsync(pdf, processing, cancellationToken).ConfigureAwait(false);
        return ExtractionDocumentMapper.Map(identifier, result);
    }

    /// <inheritdoc />
    /// <remarks>The client itself first (as the base class does), then the wrapped processor.</remarks>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return base.GetService(serviceType, serviceKey)
            ?? (serviceKey is null && serviceType.IsInstanceOfType(_processor) ? _processor : null);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _ownedProcessor?.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private static FoliantDocumentExtractionClientOptions Validate(FoliantDocumentExtractionClientOptions? options)
    {
        options ??= new FoliantDocumentExtractionClientOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxInputBytes, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxInputBytes, Array.MaxLength, nameof(options));
        return options;
    }

    private static bool IsPdf(string mediaType)
    {
        // Parameters such as "; charset=..." are ignored; the comparison is case-insensitive.
        var type = mediaType.Split(';', 2)[0].Trim();
        return string.Equals(type, PdfMediaType, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessingOptions? ReadOptions(DocumentExtractionOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        if (options.ExtractorName is not null)
        {
            throw new ArgumentException("ExtractorName is not supported by this client.", nameof(options));
        }

        ProcessingOptions? processing = null;
        foreach (var (key, value) in options.AdditionalProperties)
        {
            if (key != ProcessingOptionsKey)
            {
                throw new ArgumentException($"Option '{key}' is not supported by this client.", nameof(options));
            }

            processing = value as ProcessingOptions
                ?? throw new ArgumentException(
                    $"Option '{ProcessingOptionsKey}' must be a {nameof(ProcessingOptions)}.", nameof(options));
        }

        return processing;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        // A seekable stream that is already too long is rejected before any read. Otherwise reading
        // stops at the first chunk that would pass the limit, so at most maxBytes are buffered.
        if (stream.CanSeek && stream.Length - stream.Position > maxBytes)
        {
            throw new InvalidDataException($"The document is larger than the {maxBytes}-byte limit.");
        }

        using var buffer = stream.CanSeek
            ? new MemoryStream((int)Math.Max(0, stream.Length - stream.Position))
            : new MemoryStream();
        var chunk = new byte[CopyBufferBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (read > maxBytes - buffer.Length)
            {
                throw new InvalidDataException($"The document is larger than the {maxBytes}-byte limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        // A pre-sized buffer that filled exactly is returned as is, without a second copy.
        return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
    }
}
