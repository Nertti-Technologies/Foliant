using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;

namespace Foliant.DocumentExtraction;

/// <summary>
/// Maps a Foliant <see cref="DocumentResult"/> to the shared <see cref="ExtractionDocument"/> model
/// (ADR-0009 §3.2): one section per page, elements in Foliant's reading order, coordinates in points
/// from a top-left origin, and Foliant's review signals in metadata.
/// </summary>
/// <remarks>
/// No text is lost (Foliant's coverage invariant): every region and every page-furniture line becomes
/// an element, and table lines outside the predicted grid follow the table as text. The adapter tests
/// check that every page line's text appears in some element.
/// </remarks>
internal static class ExtractionDocumentMapper
{
    internal const string PagesNeedingReviewKey = "foliant.pagesNeedingReview";
    internal const string SensitivityMarkedPagesKey = "foliant.sensitivityMarkedPages";
    internal const string CoverageHoldsKey = "foliant.coverageHolds";
    internal const string RecallPercentKey = "foliant.recallPercent";
    internal const string PagesWithNoticeKey = "foliant.pagesWithNotice";

    private const double PointsPerInch = 72.0;

    public static ExtractionDocument Map(string identifier, DocumentResult result)
    {
        var document = new ExtractionDocument(identifier, content: null) { RawRepresentation = result };

        foreach (var page in result.Pages)
        {
            var frame = SourcePage(page);
            document.SourcePages.Add(frame);
            document.Sections.Add(MapPage(page, frame));
        }

        document.Metadata[PagesNeedingReviewKey] = result.PagesNeedingReview.ToArray();
        document.Metadata[SensitivityMarkedPagesKey] = result.SensitivityMarkedPages.ToArray();
        document.Metadata[CoverageHoldsKey] = result.Pages.All(p => p.Verification.CoverageHolds);
        document.Metadata[RecallPercentKey] = PooledRecall(result.Pages);
        // A notice can explain an empty page that is not flagged for review (e.g. a dynamic XFA form).
        document.Metadata[PagesWithNoticeKey] =
            result.Pages.Where(p => p.Notice is not null).Select(p => p.PageNumber).ToArray();
        return document;
    }

    private static ExtractionDocumentSourcePage SourcePage(PageResult page)
    {
        if (page.Dpi <= 0)
        {
            throw new InvalidDataException($"Page {page.PageNumber} reports a non-positive DPI ({page.Dpi}).");
        }

        return new ExtractionDocumentSourcePage(
            $"page-{page.PageNumber}",
            page.PageNumber,
            new ExtractionDocumentPageDimensions(ToPoints(page.WidthPx, page.Dpi), ToPoints(page.HeightPx, page.Dpi)),
            ExtractionDocumentCoordinateUnit.Point,
            ExtractionDocumentCoordinateOrigin.TopLeft);
    }

    private static ExtractionDocumentSection MapPage(PageResult page, ExtractionDocumentSourcePage frame)
    {
        // The section's empty text payload carries the per-page signals (ADR-0009 D3); it is not an
        // aggregate copy of the page's text.
        var content = new TextContent(string.Empty)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["needsReview"] = page.NeedsReview,
                ["textSource"] = page.Source.ToString(),
                ["recallPercent"] = page.Verification.RecallPercent,
                ["notice"] = page.Notice,
            },
        };
        var section = new ExtractionDocumentSection(content);

        var (headers, footers) = SplitFurniture(page);
        foreach (var line in headers)
        {
            section.Elements.Add(Furniture(line, "pageHeader", page, frame));
        }

        foreach (var region in page.Regions)
        {
            foreach (var element in MapRegion(region, page, frame))
            {
                section.Elements.Add(element);
            }
        }

        foreach (var line in footers)
        {
            section.Elements.Add(Furniture(line, "pageFooter", page, frame));
        }

        return section;
    }

    private static IEnumerable<ExtractionDocumentElement> MapRegion(
        Region region, PageResult page, ExtractionDocumentSourcePage frame)
    {
        var sources = new[] { Source(region.Bounds, page, frame, region) };
        switch (region.Type)
        {
            case RegionType.Title:
                // HeadingLevel stays null: Foliant does not infer a heading hierarchy.
                yield return Text(StripPrefix(region.Text, "## "), "heading", sources);
                break;

            case RegionType.Caption:
                yield return Text(Unwrap(region.Text, '*'), "caption", sources);
                break;

            case RegionType.Footnote:
                yield return Text(region.Text, "footnote", sources);
                break;

            case RegionType.Formula:
                yield return Text(region.Text, "formula", sources);
                break;

            case RegionType.Figure:
                var image = new ExtractionDocumentImageElement(content: null, text: region.Text);
                image.Sources.AddRange(sources);
                yield return image;
                break;

            case RegionType.Table when region.Table is { } grid:
                var (table, unplaced) = Table(grid, sources, page, frame);
                yield return table;
                foreach (var cell in unplaced)
                {
                    yield return Text(cell.Text, role: null, [Source(cell.Bounds, page, frame, cell)]);
                }

                var outside = LinesOutsideGrid(region.Text);
                if (outside.Length > 0)
                {
                    yield return Text(outside, role: null, sources);
                }

                break;

            default:
                // Text, Unknown, orphan lines and tables that degraded to prose.
                yield return Text(region.Text, role: null, sources);
                break;
        }
    }

    private static (ExtractionDocumentTableElement Table, List<TableCell> Unplaced) Table(
        TableStructure grid, ExtractionDocumentSourceReference[] sources, PageResult page, ExtractionDocumentSourcePage frame)
    {
        var table = new ExtractionDocumentTableElement(content: null, grid.RowCount, grid.ColumnCount);
        table.Sources.AddRange(sources);
        var unplaced = new List<TableCell>();
        foreach (var cell in grid.Cells)
        {
            // A cell outside the grid or on an occupied position (possible from a custom table
            // extractor) would throw or overwrite; it follows the table as text instead.
            if (cell.Row < 0 || cell.Row >= grid.RowCount || cell.Column < 0 || cell.Column >= grid.ColumnCount
                || table.Cells[cell.Row, cell.Column] is not null)
            {
                unplaced.Add(cell);
                continue;
            }

            // Foliant predicts no spans or header roles, so every cell is 1x1 with no role.
            var mapped = new ExtractionDocumentTableCell(
                new ExtractionDocumentTextElement(new TextContent(cell.Text), role: null), rowSpan: 1, columnSpan: 1, role: null);
            mapped.Sources.Add(Source(cell.Bounds, page, frame, cell));
            table.Cells[cell.Row, cell.Column] = mapped;
        }

        return (table, unplaced);
    }

    /// <summary>
    /// The lines of a table region that are outside its predicted grid. Coupled to the table
    /// renderers in Foliant.Pipeline (MarkdownComposer.RenderTable and FederalFormTableRenderer.Render):
    /// they write the grid rows (possibly none), then a blank line, then those lines. If
    /// that layout changes, this must change with it; the composer-format test in the adapter tests
    /// pins it.
    /// </summary>
    internal static string LinesOutsideGrid(string regionText)
    {
        string text = regionText.Replace("\r\n", "\n", StringComparison.Ordinal);
        int gap = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (gap >= 0)
        {
            text = text[(gap + 2)..];
        }
        else if (text.StartsWith('|'))
        {
            text = string.Empty; // a grid with nothing after it
        }

        // Otherwise no grid was written (no rows, or no columns), so the whole text is outside it.

        return text.Trim('\n');
    }

    private static ExtractionDocumentTextElement Text(
        string text, string? role, ExtractionDocumentSourceReference[] sources)
    {
        var element = new ExtractionDocumentTextElement(new TextContent(text), role);
        element.Sources.AddRange(sources);
        return element;
    }

    private static ExtractionDocumentTextElement Furniture(
        TextLine line, string role, PageResult page, ExtractionDocumentSourcePage frame)
    {
        var element = new ExtractionDocumentTextElement(new TextContent(line.Text), role);
        element.Sources.Add(Source(line.Bounds, page, frame, line));
        return element;
    }

    private static (List<TextLine> Headers, List<TextLine> Footers) SplitFurniture(PageResult page)
    {
        // A furniture line whose centre is in the top half of the page is a header, otherwise a footer.
        // Filtered twice rather than with Except, which would drop repeated identical lines.
        var headers = page.PageFurniture.Where(l => l.Bounds.CenterY < page.HeightPx / 2f).ToList();
        var footers = page.PageFurniture.Where(l => l.Bounds.CenterY >= page.HeightPx / 2f).ToList();
        return (headers, footers);
    }

    private static ExtractionDocumentSourceReference Source(
        BoundingBox box, PageResult page, ExtractionDocumentSourcePage frame, object raw)
    {
        double x1 = ToPoints(box.X1, page.Dpi), y1 = ToPoints(box.Y1, page.Dpi);
        double x2 = ToPoints(box.X2, page.Dpi), y2 = ToPoints(box.Y2, page.Dpi);
        return new ExtractionDocumentSourceReference(
            frame,
            [new(x1, y1), new(x2, y1), new(x2, y2), new(x1, y2)],
            raw);
    }

    private static double ToPoints(double pixels, int dpi) => pixels * PointsPerInch / dpi;

    private static double? PooledRecall(IReadOnlyList<PageResult> pages)
    {
        // Word recall pooled over the pages that have a text layer to score against.
        long truth = pages.Sum(p => (long)p.Verification.TruthWords);
        long found = pages.Sum(p => (long)p.Verification.TruthWordsFound);
        return truth > 0 ? 100.0 * found / truth : null;
    }

    private static string StripPrefix(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;

    private static string Unwrap(string text, char marker) =>
        text.Length >= 2 && text[0] == marker && text[^1] == marker ? text[1..^1] : text;
}
