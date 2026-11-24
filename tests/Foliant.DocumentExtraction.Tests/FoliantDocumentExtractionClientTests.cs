using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Xunit;

namespace Foliant.DocumentExtraction.Tests;

/// <summary>
/// The adapter's contract (ADR-0009): input validation, option handling, the mapping to the shared
/// document model, and no text loss. A fake processor stands in for the ML pipeline, so these tests
/// need no models.
/// </summary>
public sealed class FoliantDocumentExtractionClientTests
{
    private const int Dpi = 144; // 2 pixels per point, so expected coordinates are easy to read

    // --- input validation -----------------------------------------------------------------------

    [Fact]
    public async Task Rejects_non_pdf_media_type()
    {
        using var client = new FoliantDocumentExtractionClient(new FakeProcessor(Result()));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.ExtractAsync(new MemoryStream([1]), "doc", "image/png"));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("Application/PDF")]
    [InlineData("application/pdf; name=a.pdf")]
    public async Task Accepts_pdf_media_type_variants(string mediaType)
    {
        using var client = new FoliantDocumentExtractionClient(new FakeProcessor(Result()));
        var document = await client.ExtractAsync(new MemoryStream([1]), "doc", mediaType);
        Assert.Equal("doc", document.Identifier);
    }

    [Fact]
    public async Task Rejects_blank_identifier_and_null_stream()
    {
        using var client = new FoliantDocumentExtractionClient(new FakeProcessor(Result()));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            client.ExtractAsync(new MemoryStream([1]), " ", "application/pdf"));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ExtractAsync(null!, "doc", "application/pdf"));
    }

    [Fact]
    public async Task Rejects_stream_over_the_limit_without_processing()
    {
        var processor = new FakeProcessor(Result());
        using var client = new FoliantDocumentExtractionClient(
            processor, new FoliantDocumentExtractionClientOptions { MaxInputBytes = 10 });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ExtractAsync(new MemoryStream(new byte[11]), "doc", "application/pdf"));
        Assert.Equal(0, processor.Calls);

        await client.ExtractAsync(new MemoryStream(new byte[10]), "doc", "application/pdf");
        Assert.Equal(10, processor.LastInput!.Length);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void Rejects_limit_outside_the_buffer_range(long limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FoliantDocumentExtractionClient(
            new FakeProcessor(Result()), new FoliantDocumentExtractionClientOptions { MaxInputBytes = limit }));
    }

    [Fact]
    public async Task Rejects_seekable_stream_over_the_limit_before_reading()
    {
        var stream = new CountingStream(new byte[100]);
        using var client = new FoliantDocumentExtractionClient(
            new FakeProcessor(Result()), new FoliantDocumentExtractionClientOptions { MaxInputBytes = 10 });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ExtractAsync(stream, "doc", "application/pdf"));
        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public async Task Disposed_client_throws()
    {
        var client = new FoliantDocumentExtractionClient(new FakeProcessor(Result()));
        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.ExtractAsync(new MemoryStream([1]), "doc", "application/pdf"));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var client = new FoliantDocumentExtractionClient(new FakeProcessor(Result()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExtractAsync(new MemoryStream([1]), "doc", "application/pdf", cancellationToken: cts.Token));
    }

    // --- options --------------------------------------------------------------------------------

    [Fact]
    public async Task Per_call_processing_options_override_the_client_default()
    {
        var clientDefault = new ProcessingOptions();
        var perCall = new ProcessingOptions();
        var processor = new FakeProcessor(Result());
        using var client = new FoliantDocumentExtractionClient(
            processor, new FoliantDocumentExtractionClientOptions { Processing = clientDefault });

        await client.ExtractAsync(new MemoryStream([1]), "doc", "application/pdf");
        Assert.Same(clientDefault, processor.LastOptions);

        var options = new DocumentExtractionOptions();
        options.AdditionalProperties[FoliantDocumentExtractionClient.ProcessingOptionsKey] = perCall;
        await client.ExtractAsync(new MemoryStream([1]), "doc", "application/pdf", options);
        Assert.Same(perCall, processor.LastOptions);
    }

    [Fact]
    public async Task Unknown_or_mistyped_options_are_rejected()
    {
        using var client = new FoliantDocumentExtractionClient(new FakeProcessor(Result()));

        var unknown = new DocumentExtractionOptions();
        unknown.AdditionalProperties["other"] = 1;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExtractAsync(new MemoryStream([1]), "doc", "application/pdf", unknown));

        var mistyped = new DocumentExtractionOptions();
        mistyped.AdditionalProperties[FoliantDocumentExtractionClient.ProcessingOptionsKey] = "fast";
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExtractAsync(new MemoryStream([1]), "doc", "application/pdf", mistyped));

        var named = new DocumentExtractionOptions { ExtractorName = "other" };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExtractAsync(new MemoryStream([1]), "doc", "application/pdf", named));
    }

    [Fact]
    public void GetService_returns_the_client_first_then_the_processor()
    {
        var processor = new FakeProcessor(Result());
        using var client = new FoliantDocumentExtractionClient(processor);
        Assert.Same(processor, client.GetService(typeof(IDocumentProcessor)));
        Assert.Same(client, client.GetService(typeof(object)));
        Assert.Same(client, client.GetService(typeof(IDisposable)));
        Assert.Null(client.GetService(typeof(IDocumentProcessor), "key"));
    }

    // --- mapping --------------------------------------------------------------------------------

    [Fact]
    public async Task Page_frame_is_in_points_from_top_left()
    {
        var document = await Extract(Result(Page(1, regions: [Text("Body", 20, 40, 220, 80)])));

        var frame = Assert.Single(document.SourcePages);
        Assert.Equal(1, frame.PageNumber);
        Assert.Equal(ExtractionDocumentCoordinateUnit.Point, frame.CoordinateUnit);
        Assert.Equal(ExtractionDocumentCoordinateOrigin.TopLeft, frame.CoordinateOrigin);
        var size = frame.Dimensions!.Value;
        Assert.Equal(306, size.Width);  // 612 px at 144 dpi
        Assert.Equal(396, size.Height); // 792 px at 144 dpi

        var element = Assert.Single(Assert.Single(document.Sections).Elements);
        var polygon = Assert.Single(element.Sources).Polygon;
        Assert.Equal([(10d, 20d), (110d, 20d), (110d, 40d), (10d, 40d)], polygon.Select(p => (p.X, p.Y)));
    }

    [Fact]
    public async Task Roles_follow_region_types_and_markdown_markers_are_removed()
    {
        var document = await Extract(Result(Page(1, regions:
        [
            Region(RegionType.Title, "## Annual report"),
            Region(RegionType.Text, "Plain paragraph"),
            Region(RegionType.Caption, "*Figure 1*"),
            Region(RegionType.Footnote, "1. Source"),
            Region(RegionType.Formula, "E = mc^2"),
        ])));

        var elements = Assert.Single(document.Sections).Elements.Cast<ExtractionDocumentTextElement>().ToList();
        Assert.Equal(["heading", null, "caption", "footnote", "formula"], elements.Select(e => e.Role));
        Assert.Equal(["Annual report", "Plain paragraph", "Figure 1", "1. Source", "E = mc^2"],
            elements.Select(TextOf));
    }

    [Fact]
    public async Task Figure_becomes_an_image_with_its_text()
    {
        var document = await Extract(Result(Page(1, regions: [Region(RegionType.Figure, "Chart label")])));
        var image = Assert.IsType<ExtractionDocumentImageElement>(Assert.Single(document.Sections[0].Elements));
        Assert.Equal("Chart label", image.Text);
    }

    [Fact]
    public async Task Table_grid_is_mapped_and_lines_outside_the_grid_are_kept()
    {
        var grid = new TableStructure(2, 2,
        [
            new TableCell(0, 0, "Item", Box(0, 0, 50, 20)),
            new TableCell(0, 1, "Cost", Box(50, 0, 100, 20)),
            new TableCell(1, 0, "Pen", Box(0, 20, 50, 40)),
            new TableCell(1, 1, "2", Box(50, 20, 100, 40)),
        ]);
        var table = new Region(RegionType.Table, "table", Box(0, 0, 100, 60),
            "| Item | Cost |\n|---|---|\n| Pen | 2 |\n\nTotal excludes tax", grid, 0.9f);

        var elements = (await Extract(Result(Page(1, regions: [table])))).Sections[0].Elements;

        Assert.Equal(2, elements.Count);
        var mapped = Assert.IsType<ExtractionDocumentTableElement>(elements[0]);
        Assert.Equal((2, 2), (mapped.Cells.RowCount, mapped.Cells.ColumnCount));
        var cell = mapped.Cells[1, 0]!;
        Assert.Equal("Pen", TextOf(Assert.IsType<ExtractionDocumentTextElement>(cell.Content)));
        Assert.Equal((1, 1), (cell.RowSpan, cell.ColumnSpan));
        Assert.Equal("Total excludes tax", TextOf(Assert.IsType<ExtractionDocumentTextElement>(elements[1])));
    }

    [Fact]
    public async Task Furniture_is_split_into_headers_and_footers_and_repeats_are_kept()
    {
        var page = Page(1,
            regions: [Region(RegionType.Text, "Body")],
            furniture:
            [
                Line("Running head", 0, 10), Line("Confidential", 0, 700), Line("Confidential", 0, 740),
            ]);

        var elements = (await Extract(Result(page))).Sections[0].Elements.Cast<ExtractionDocumentTextElement>();

        Assert.Equal(
            [("pageHeader", "Running head"), (null, "Body"), ("pageFooter", "Confidential"), ("pageFooter", "Confidential")],
            elements.Select(e => (e.Role, TextOf(e))));
    }

    // Pins the parsing side of the table-layout contract (MarkdownComposer.RenderTable's test pins the
    // producing side): grid rows, a blank line, then the lines outside the grid, with either line ending.
    [Theory]
    [InlineData("| a | b |\n|---|---|\n\n| Note: totals exclude tax\nSecond line\n", 1, "| Note: totals exclude tax\nSecond line")]
    [InlineData("| a | b |\r\n|---|---|\r\n\r\nTotal\r\n", 1, "Total")]
    [InlineData("| a | b |\n|---|---|\n", 1, "")]
    [InlineData("only outside\n", 0, "only outside")]
    [InlineData("rows but no columns, nothing written\nsecond\n", 2, "rows but no columns, nothing written\nsecond")]
    public void Lines_outside_the_grid_are_recovered(string text, int rows, string expected)
    {
        _ = rows; // the layout, not the grid's size, decides: some renderers write no grid for 0 columns
        Assert.Equal(expected, ExtractionDocumentMapper.LinesOutsideGrid(text));
    }

    [Fact]
    public async Task Cells_outside_the_grid_or_on_taken_positions_follow_the_table_as_text()
    {
        var grid = new TableStructure(1, 1,
        [
            new TableCell(0, 0, "first", Box(0, 0, 10, 10)),
            new TableCell(0, 0, "second", Box(0, 0, 10, 10)),
            new TableCell(3, 5, "stray", Box(0, 0, 10, 10)),
        ]);
        var table = new Region(RegionType.Table, "table", Box(0, 0, 100, 60), "| first |\n|---|\n", grid, 0.9f);

        var elements = (await Extract(Result(Page(1, regions: [table])))).Sections[0].Elements;

        var mapped = Assert.IsType<ExtractionDocumentTableElement>(elements[0]);
        Assert.Equal("first", TextOf(Assert.IsType<ExtractionDocumentTextElement>(mapped.Cells[0, 0]!.Content)));
        Assert.Equal(["second", "stray"], elements.Skip(1).Cast<ExtractionDocumentTextElement>().Select(TextOf));
    }

    [Fact]
    public async Task Pages_with_a_notice_are_listed_even_when_not_flagged()
    {
        var xfa = Page(1, regions: []) with { Notice = "dynamic XFA form" };
        var document = await Extract(Result(xfa, Page(2, regions: [Region(RegionType.Text, "B")])));
        Assert.Equal(new[] { 1 }, document.Metadata["foliant.pagesWithNotice"]);
        Assert.Equal(Array.Empty<int>(), document.Metadata["foliant.pagesNeedingReview"]);
    }

    [Fact]
    public async Task Review_signals_are_in_document_and_section_metadata()
    {
        var reviewed = Page(1, regions: [Region(RegionType.Text, "A")]) with
        {
            NeedsReview = true, Source = TextSource.Ocr, Notice = "low text yield",
            Verification = new PageVerification(0, 0, 0, 0),
        };
        var marked = Page(2, regions: [Region(RegionType.Text, "B")]) with
        {
            SensitivityMarking = "CONFIDENTIAL", Verification = new PageVerification(1, 10, 9, 0),
        };
        var result = Result(reviewed, marked);

        var document = await Extract(result);

        Assert.Same(result, document.RawRepresentation);
        Assert.Equal(new[] { 1 }, document.Metadata["foliant.pagesNeedingReview"]);
        Assert.Equal(new[] { 2 }, document.Metadata["foliant.sensitivityMarkedPages"]);
        Assert.Equal(false, document.Metadata["foliant.coverageHolds"]);
        Assert.Equal(90.0, document.Metadata["foliant.recallPercent"]);

        var signals = Assert.IsType<TextContent>(document.Sections[0].Content).AdditionalProperties!;
        Assert.Equal(true, signals["needsReview"]);
        Assert.Equal("Ocr", signals["textSource"]);
        Assert.Null(signals["recallPercent"]);
        Assert.Equal("low text yield", signals["notice"]);
    }

    [Fact]
    public async Task Recall_is_null_when_no_page_has_a_text_layer()
    {
        var document = await Extract(Result(Page(1, regions: [Region(RegionType.Text, "A")])));
        Assert.Null(document.Metadata["foliant.recallPercent"]);
        Assert.Equal(true, document.Metadata["foliant.coverageHolds"]);
    }

    [Fact]
    public async Task Non_positive_dpi_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Extract(Result(Page(1, regions: []) with { Dpi = 0 })));
    }

    [Fact]
    public async Task No_page_line_is_lost()
    {
        var lines = new[] { Line("Heading text", 0, 100), Line("Paragraph one", 0, 200), Line("Cell text", 0, 300) };
        var grid = new TableStructure(1, 2,
            [new TableCell(0, 0, "Cell text", Box(0, 290, 50, 310)), new TableCell(0, 1, "", Box(50, 290, 100, 310))]);
        var page = Page(1,
            regions:
            [
                Region(RegionType.Title, "## Heading text"),
                Region(RegionType.Text, "Paragraph one"),
                new Region(RegionType.Table, "table", Box(0, 280, 100, 320), "| Cell text |  |\n|---|---|", grid, 1f),
            ],
            furniture: [Line("Page 1", 0, 760)]) with { Lines = lines };

        var document = await Extract(Result(page));

        var texts = AllText(document.Sections[0]).ToList();
        foreach (var line in page.Lines.Concat(page.PageFurniture))
        {
            Assert.Contains(texts, t => t.Contains(line.Text, StringComparison.Ordinal));
        }
    }

    // --- helpers --------------------------------------------------------------------------------

    private static async Task<ExtractionDocument> Extract(DocumentResult result)
    {
        using var client = new FoliantDocumentExtractionClient(new FakeProcessor(result));
        return await client.ExtractAsync(new MemoryStream([1]), "doc", "application/pdf");
    }

    private static IEnumerable<string> AllText(ExtractionDocumentElement element) => element switch
    {
        ExtractionDocumentSection s => s.Elements.SelectMany(AllText),
        ExtractionDocumentTableElement t => t.Cells.Where(c => c?.Content is not null).SelectMany(c => AllText(c!.Content!)),
        ExtractionDocumentTextElement x => [TextOf(x)],
        ExtractionDocumentImageElement i => [i.Text ?? ""],
        _ => [],
    };

    private static string TextOf(ExtractionDocumentTextElement element) =>
        Assert.IsType<TextContent>(element.Content).Text;

    private static DocumentResult Result(params PageResult[] pages) =>
        new(pages.Length == 0 ? [Page(1, regions: [])] : pages, "");

    private static PageResult Page(int number, Region[] regions, TextLine[]? furniture = null) =>
        new(number, 612, 792, Dpi, regions, [], furniture ?? [], TextSource.TextLayer, "",
            new PageVerification(0, 0, 0, 0));

    private static Region Region(RegionType type, string text) =>
        new(type, type.ToString(), Box(0, 100, 100, 120), text, null, 0.9f);

    private static Region Text(string text, float x1, float y1, float x2, float y2) =>
        new(RegionType.Text, "text", Box(x1, y1, x2, y2), text, null, 0.9f);

    private static TextLine Line(string text, float x, float y) =>
        new(Box(x, y, x + 100, y + 20), text, 1f, TextSource.TextLayer);

    private static BoundingBox Box(float x1, float y1, float x2, float y2) => new(x1, y1, x2, y2);

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int Reads { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class FakeProcessor(DocumentResult result) : IDocumentProcessor
    {
        public int Calls { get; private set; }
        public byte[]? LastInput { get; private set; }
        public ProcessingOptions? LastOptions { get; private set; }

        public Task<DocumentResult> ProcessAsync(
            byte[] pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastInput = pdf;
            LastOptions = options;
            return Task.FromResult(result);
        }

        public Task<DocumentResult> ProcessAsync(
            Stream pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The adapter buffers the stream and calls the byte[] overload.");
    }
}
