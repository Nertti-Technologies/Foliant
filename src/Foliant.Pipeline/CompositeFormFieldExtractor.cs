// Composes form-field extractors: tries each in order and returns the first that yields fields.
// Wired as AcroForm-first (exact values from a fillable dictionary) with the geometric profile
// extractor as the fallback for flattened/scanned forms — so a page uses the most trustworthy
// source available without the caller having to branch.

namespace Foliant.Pipeline;

public sealed class CompositeFormFieldExtractor : IFormFieldExtractor
{
    private readonly IFormFieldExtractor[] _extractors;

    /// <param name="extractors">Tried in order; the first to return any field wins.</param>
    public CompositeFormFieldExtractor(params IFormFieldExtractor[] extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors;
    }

    public IReadOnlyList<FormField> Extract(
        byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines) =>
        Extract(pdf, pageNumber, image, lines, PageFrame.Of(image));

    /// <remarks>Passes <paramref name="frame"/> to each extractor.</remarks>
    public IReadOnlyList<FormField> Extract(
        byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines, PageFrame frame)
    {
        foreach (var extractor in _extractors)
        {
            var fields = extractor.Extract(pdf, pageNumber, image, lines, frame);
            if (fields.Count > 0) return fields;
        }
        return Array.Empty<FormField>();
    }
}
