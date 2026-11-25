using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

// Form-field extractors work in the page's upright PDF frame; DocumentProcessor hands them the raster
// at its actual DPI and, on a page the orientation detector rotated, rotated back.
public sealed class FormFieldFrameTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotateClockwise_MatchesTheRasterRotation(int degrees)
    {
        // A 40x20 white raster with one dark pixel at (30, 5): find where the raster rotation puts it.
        const int w = 40, h = 20, x = 30, y = 5;
        var pixels = Enumerable.Repeat((byte)255, w * h * 4).ToArray();
        int o = (y * w + x) * 4;
        pixels[o] = pixels[o + 1] = pixels[o + 2] = 0;

        var rotated = ScanDegrader.Rotate(degrees).Transform(new PageImage(w, h, 72, pixels));
        var (rx, ry) = DarkestPixel(rotated);

        var box = DocumentProcessor.RotateClockwise(new BoundingBox(x, y, x + 1, y + 1), degrees, w, h);
        Assert.Equal(new BoundingBox(rx, ry, rx + 1, ry + 1), box);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotateClockwise_ThenBack_IsTheIdentity(int degrees)
    {
        var box = new BoundingBox(3, 4, 10, 7);
        bool swap = degrees is 90 or 270;
        var turned = DocumentProcessor.RotateClockwise(box, degrees, 40, 20);
        var back = DocumentProcessor.RotateClockwise(turned, 360 - degrees, swap ? 20 : 40, swap ? 40 : 20);
        Assert.Equal(box, back);
    }

    [Fact]
    public void ExtractFormFields_OnARotatedPage_RunsUprightAndMapsBoundsBack()
    {
        // The raster was rotated 90 degrees clockwise to bring the page upright: 20x40 now, 40x20 as rendered.
        var image = new PageImage(20, 40, 72, new byte[20 * 40 * 4]);
        var line = new TextLine(new BoundingBox(5, 30, 15, 35), "LABEL", 1f, TextSource.Ocr);
        var extractor = new EchoExtractor();

        var field = Assert.Single(DocumentProcessor.ExtractFormFields(
            extractor, [], 1, image, dpi: 144, rotation: 90, [line]));

        Assert.Equal((40, 20, 144), (extractor.Image!.Width, extractor.Image.Height, extractor.Image.Dpi));
        Assert.Equal(DocumentProcessor.RotateClockwise(line.Bounds, 270, 20, 40), extractor.Lines![0].Bounds);
        Assert.Equal(line.Bounds, field.Bounds);   // the echoed box comes back in the page's raster
    }

    [Fact]
    public void ExtractFormFields_OnAnUprightPage_PassesTheRasterThrough()
    {
        var image = new PageImage(20, 40, 72, new byte[20 * 40 * 4]);
        var extractor = new EchoExtractor();

        DocumentProcessor.ExtractFormFields(extractor, [], 1, image, dpi: 72, rotation: 0, []);

        Assert.Same(image, extractor.Image);
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

    // Returns one field whose bounds are its first line's bounds, so the mapping back can be checked.
    private sealed class EchoExtractor : IFormFieldExtractor
    {
        public PageImage? Image;
        public IReadOnlyList<TextLine>? Lines;

        public IReadOnlyList<FormField> Extract(byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines)
        {
            (Image, Lines) = (image, lines);
            return lines.Count == 0 ? [] : [new FormField("label", "value", FieldKind.Text, lines[0].Bounds)];
        }
    }
}
