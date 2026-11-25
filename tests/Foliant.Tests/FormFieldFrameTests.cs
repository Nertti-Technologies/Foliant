using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

// Form-field extractors get the page's final raster and lines; PageFrame says how that raster was turned
// upright and deskewed, so geometry computed from PDF coordinates can be mapped onto it.
public sealed class FormFieldFrameTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void FromRendered_MatchesTheRasterRotation(int degrees)
    {
        // A 40x20 white raster with one dark pixel at (30, 5): find where the raster rotation puts it.
        const int w = 40, h = 20, x = 30, y = 5;
        var pixels = Enumerable.Repeat((byte)255, w * h * 4).ToArray();
        int o = (y * w + x) * 4;
        pixels[o] = pixels[o + 1] = pixels[o + 2] = 0;

        var rotated = ScanDegrader.Rotate(degrees).Transform(new PageImage(w, h, 72, pixels));
        var (rx, ry) = DarkestPixel(rotated);

        var frame = new PageFrame(w, h, degrees);
        Assert.Equal((rotated.Width, rotated.Height), (frame.Width, frame.Height));
        Assert.Equal(new BoundingBox(rx, ry, rx + 1, ry + 1), frame.FromRendered(new BoundingBox(x, y, x + 1, y + 1)));
    }

    [Fact]
    public void FromRendered_FollowsTheDefaultPreprocessorDeskew()
    {
        // Text-like dark bars tilted 3 degrees plus a compact red marker, deskewed by the real preprocessor.
        const int w = 800, h = 600;
        var pixels = Enumerable.Repeat((byte)255, w * h * 4).ToArray();
        void Paint(int x, int y, byte b, byte g, byte r)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int o = (y * w + x) * 4;
            (pixels[o], pixels[o + 1], pixels[o + 2]) = (b, g, r);
        }
        double tan = Math.Tan(3 * Math.PI / 180);
        for (int row = 200; row < 520; row += 24)
            for (int x = 40; x < w - 40; x++)
                for (int t = 0; t < 8; t++)
                    Paint(x, row + t + (int)Math.Round((x - w / 2) * tan), 0, 0, 0);
        for (int y = 60; y < 90; y++)
            for (int x = 100; x < 130; x++)
                Paint(x, y, 0, 0, 255);

        var processed = new DefaultPagePreprocessor().Process(new PageImage(w, h, 300, pixels));
        Assert.NotEqual(0f, processed.SkewCorrectedDegrees);
        Assert.Equal((w, h), (processed.Image.Width, processed.Image.Height));

        var marker = new BoundingBox(100, 60, 130, 90);
        var (cx, cy) = RedCentroid(processed.Image);
        var mapped = new PageFrame(w, h, 0, processed.SkewCorrectedDegrees).FromRendered(marker);
        var wrongWay = new PageFrame(w, h, 0, -processed.SkewCorrectedDegrees).FromRendered(marker);
        Assert.True(Inside(mapped, cx, cy), $"centroid ({cx}, {cy}) outside {mapped}");
        Assert.False(Inside(wrongWay, cx, cy), $"centroid ({cx}, {cy}) inside the opposite rotation {wrongWay}");
    }

    [Fact]
    public void FromRendered_ClipsADeskewedBoxToTheRaster() =>
        Assert.Equal(new BoundingBox(0, 0, 10, 10),
            new PageFrame(10, 10, 0, -45f).FromRendered(new BoundingBox(0, 0, 10, 10)));

    [Theory]
    [InlineData(0, 10, 0, 0f)]
    [InlineData(10, 0, 0, 0f)]
    [InlineData(10, 10, 45, 0f)]
    [InlineData(10, 10, -90, 0f)]
    [InlineData(10, 10, 0, float.NaN)]
    [InlineData(10, 10, 0, float.PositiveInfinity)]
    public void PageFrame_RejectsInvalidValues(int w, int h, int orientation, float skew) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageFrame(w, h, orientation, skew));

    [Fact]
    public void ExtractFormFields_PassesTheFinalRasterAndLines_WithTheFrame()
    {
        // The raster was turned 90 degrees clockwise to bring the page upright: 20x40 now, 40x20 as rendered.
        var image = new PageImage(20, 40, 72, new byte[20 * 40 * 4]);
        var line = new TextLine(new BoundingBox(5, 30, 15, 35), "LABEL", 1f, TextSource.Ocr);
        var extractor = new RecordingExtractor();

        DocumentProcessor.ExtractFormFields(extractor, [], 1, image, dpi: 144, orientation: 90, deskewDegrees: 2.5f, [line]);

        Assert.Equal((20, 40, 144), (extractor.Image!.Width, extractor.Image.Height, extractor.Image.Dpi));
        Assert.Same(image.PixelsBgra8888, extractor.Image.PixelsBgra8888);   // rewrapped, not copied
        Assert.Equal(line.Bounds, Assert.Single(extractor.Lines!).Bounds);   // lines stay in the final frame
        Assert.Equal(new PageFrame(40, 20, 90, 2.5f), extractor.Frame);
    }

    [Fact]
    public void WidgetExtractor_MapsWidgetBoxesThroughTheFrame()
    {
        // 100x100 pt page at 72 DPI; the widget /Rect (10,70)-(90,90) is (10,10)-(90,30) in the rendered
        // raster, and (70,10)-(90,90) after a 90-degree turn.
        byte[] pdf = FilledFormPdf.Build();
        var image = new PageImage(100, 100, 72, new byte[100 * 100 * 4]);
        var extractor = new WidgetFormFieldExtractor();

        var upright = Assert.Single(extractor.Extract(pdf, 1, image, []));
        var turned = Assert.Single(extractor.Extract(pdf, 1, image, [], new PageFrame(100, 100, 90)));

        Assert.Equal(new BoundingBox(10, 10, 90, 30), upright.Bounds);
        Assert.Equal(new BoundingBox(70, 10, 90, 90), turned.Bounds);
    }

    [Fact]
    public void CompositeExtractor_PassesTheFrameToEachExtractor()
    {
        var first = new RecordingExtractor { Returns = false };
        var second = new RecordingExtractor();
        var frame = new PageFrame(100, 50, 180, 1f);

        new CompositeFormFieldExtractor(first, second)
            .Extract([], 1, new PageImage(100, 50, 72, new byte[100 * 50 * 4]), [], frame);

        Assert.Same(frame, first.Frame);
        Assert.Same(frame, second.Frame);
    }

    [Fact]
    public void AnExtractorWithoutTheFrameOverload_StillGetsCalled()
    {
        IFormFieldExtractor extractor = new FourArgumentExtractor();
        var fields = extractor.Extract([], 1, new PageImage(1, 1, 72, new byte[4]), [], new PageFrame(1, 1, 90));
        Assert.Single(fields);
    }

    private static bool Inside(BoundingBox b, double x, double y) => x >= b.X1 && x <= b.X2 && y >= b.Y1 && y <= b.Y2;

    private static (double X, double Y) RedCentroid(PageImage image)
    {
        double sx = 0, sy = 0;
        int n = 0;
        var p = image.PixelsBgra8888;
        for (int i = 0; i < image.Width * image.Height; i++)
            if (p[i * 4 + 2] > 150 && p[i * 4 + 1] < 100 && p[i * 4] < 100)
                (sx, sy, n) = (sx + i % image.Width, sy + i / image.Width, n + 1);
        Assert.True(n > 0, "marker not found");
        return (sx / n, sy / n);
    }

    private static (int X, int Y) DarkestPixel(PageImage image)
    {
        int best = 0, bestLuma = int.MaxValue;
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            int luma = image.PixelsBgra8888[i * 4] + image.PixelsBgra8888[i * 4 + 1] + image.PixelsBgra8888[i * 4 + 2];
            if (luma < bestLuma) (best, bestLuma) = (i, luma);
        }

        return (best % image.Width, best / image.Width);
    }

    // Records what it was given; returns one field unless told not to.
    private sealed class RecordingExtractor : IFormFieldExtractor
    {
        public bool Returns { get; init; } = true;
        public PageImage? Image;
        public IReadOnlyList<TextLine>? Lines;
        public PageFrame? Frame;

        public IReadOnlyList<FormField> Extract(byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines) =>
            Extract(pdf, pageNumber, image, lines, PageFrame.Of(image));

        public IReadOnlyList<FormField> Extract(
            byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines, PageFrame frame)
        {
            (Image, Lines, Frame) = (image, lines, frame);
            return Returns ? [new FormField("label", "value", FieldKind.Text, null)] : [];
        }
    }

    private sealed class FourArgumentExtractor : IFormFieldExtractor
    {
        public IReadOnlyList<FormField> Extract(byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines) =>
            [new FormField("label", "value", FieldKind.Text, null)];
    }
}
