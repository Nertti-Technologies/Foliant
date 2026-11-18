using Foliant.Mcp.Extraction;
using Foliant.Mcp.Shaping;

namespace Foliant.Mcp.Tools;

/// <summary>A PDF read from disk for a tool call: its bytes and page count, or a shaped error.</summary>
public sealed record PdfInput(byte[] Bytes, int TotalPages, string? Error)
{
    /// <summary>
    /// Reads <paramref name="path"/> and counts its pages. An unreadable or invalid file becomes a
    /// shaped error the client can act on; cancellation is not an input error and propagates.
    /// </summary>
    public static async Task<PdfInput> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return Failed($"File not found: {path}");

        try
        {
            byte[] pdf = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return new PdfInput(pdf, Pdf.GetPageCount(pdf), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any I/O or PDF-parse failure is reported, not thrown: the tool contract is a JSON reply.
            return Failed($"Could not read PDF '{path}': {ex.Message}");
        }
    }

    private static PdfInput Failed(string message) => new([], 0, Shape.Error(message));
}
