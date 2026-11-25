namespace Foliant;

/// <summary>Result of page-image preprocessing, with a report of what was corrected.</summary>
/// <param name="Image">The cleaned page.</param>
/// <param name="SkewCorrectedDegrees">
/// The skew removed, in degrees, clockwise positive: the page was rotated by minus this angle about its
/// centre, and <paramref name="Image"/> keeps the input's size. The pipeline relies on this to map
/// PDF-derived form-field geometry onto the deskewed page (<see cref="PageFrame"/>); a preprocessor
/// that rotates any other way reports 0.
/// </param>
/// <param name="ContrastStretched">Whether contrast was normalised.</param>
/// <param name="Denoised">Whether isolated specks were removed.</param>
/// <param name="WatermarkSuppressed">Whether a coloured watermark overlay was suppressed.</param>
public sealed record PreprocessedPage(
    PageImage Image,
    float SkewCorrectedDegrees,
    bool ContrastStretched,
    bool Denoised,
    bool WatermarkSuppressed = false)
{
    public bool Changed =>
        Math.Abs(SkewCorrectedDegrees) > 0f || ContrastStretched || Denoised || WatermarkSuppressed;
}

/// <summary>
/// Cleans rendered page images before OCR and layout detection on the scanned-page path
/// (deskew, contrast normalization, despeckle). Born-digital renders are already clean and
/// skip preprocessing entirely — the pipeline only invokes this when characters must come
/// from pixels.
/// </summary>
public interface IPagePreprocessor
{
    PreprocessedPage Process(PageImage page);
}
