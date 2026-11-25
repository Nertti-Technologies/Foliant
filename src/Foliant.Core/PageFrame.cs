namespace Foliant;

/// <summary>
/// How a page's final raster relates to the page as rendered. The pipeline may turn a scanned page
/// upright (a clockwise quarter turn) and then deskew it (a small rotation about the centre that keeps
/// the size). Text lines and the raster an <see cref="IFormFieldExtractor"/> receives are in the final
/// frame; geometry computed from PDF coordinates (PDF points x DPI / 72) is in the rendered frame and
/// goes through <see cref="FromRendered"/> to line up with them.
/// </summary>
public sealed record PageFrame
{
    /// <param name="renderedWidth">Width in pixels of the page as rendered, before any rotation.</param>
    /// <param name="renderedHeight">Height in pixels of the page as rendered, before any rotation.</param>
    /// <param name="orientationDegrees">Clockwise turn applied to bring the page upright: 0, 90, 180 or 270.</param>
    /// <param name="deskewDegrees">
    /// Skew removed after that turn, clockwise positive, as <see cref="PreprocessedPage.SkewCorrectedDegrees"/>
    /// reports it: the page was rotated by minus this angle about its centre. Must be finite.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive, the turn is not a quarter turn,
    /// or the skew is not finite.</exception>
    public PageFrame(int renderedWidth, int renderedHeight, int orientationDegrees = 0, float deskewDegrees = 0f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(renderedWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(renderedHeight);
        if (orientationDegrees is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(orientationDegrees), orientationDegrees, "Only 0, 90, 180 and 270 are supported.");
        if (!float.IsFinite(deskewDegrees))
            throw new ArgumentOutOfRangeException(nameof(deskewDegrees), deskewDegrees, "The skew must be finite.");
        (RenderedWidth, RenderedHeight, OrientationDegrees, DeskewDegrees) =
            (renderedWidth, renderedHeight, orientationDegrees, deskewDegrees);
    }

    public int RenderedWidth { get; }
    public int RenderedHeight { get; }
    public int OrientationDegrees { get; }
    public float DeskewDegrees { get; }

    /// <summary>Width of the final raster.</summary>
    public int Width => OrientationDegrees is 90 or 270 ? RenderedHeight : RenderedWidth;

    /// <summary>Height of the final raster.</summary>
    public int Height => OrientationDegrees is 90 or 270 ? RenderedWidth : RenderedHeight;

    /// <summary>The frame of a raster that was neither turned nor deskewed.</summary>
    public static PageFrame Of(PageImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new PageFrame(image.Width, image.Height);
    }

    /// <summary>
    /// Maps a box from the page as rendered into the final raster: the quarter turn, then the deskew.
    /// A deskewed box is the axis-aligned bounds of the rotated box, clipped to the raster, so it grows
    /// by about (width x sin skew) in height and (height x sin skew) in width.
    /// </summary>
    public BoundingBox FromRendered(BoundingBox box)
    {
        var turned = RotateClockwise(box, OrientationDegrees, RenderedWidth, RenderedHeight);
        // Below 1e-6 degrees a point 100,000 px from the centre moves under 0.002 px (arithmetic), so the
        // box is returned unrotated and unclipped.
        return Math.Abs(DeskewDegrees) < 1e-6f ? turned : RotateAboutCentre(turned, -DeskewDegrees, Width, Height);
    }

    // Exact for a quarter turn: the box of a raster rotated clockwise by `degrees`, matching the pixel
    // permutation of a raster turned the same way (width and height swap at 90 and 270).
    private static BoundingBox RotateClockwise(BoundingBox b, int degrees, int width, int height) => degrees switch
    {
        90 => new BoundingBox(height - b.Y2, b.X1, height - b.Y1, b.X2),
        180 => new BoundingBox(width - b.X2, height - b.Y2, width - b.X1, height - b.Y1),
        270 => new BoundingBox(b.Y1, width - b.X2, b.Y2, width - b.X1),
        _ => b,
    };

    // Rotation clockwise by `degrees` about the centre of a raster that keeps its size (y down), as
    // SKCanvas.RotateDegrees after translating to the centre: the deskew geometry.
    private static BoundingBox RotateAboutCentre(BoundingBox b, float degrees, int width, int height)
    {
        double rad = degrees * Math.PI / 180.0, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = width / 2.0, cy = height / 2.0;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var (x, y) in (ReadOnlySpan<(double, double)>)[(b.X1, b.Y1), (b.X2, b.Y1), (b.X1, b.Y2), (b.X2, b.Y2)])
        {
            double dx = x - cx, dy = y - cy;
            double rx = cx + cos * dx - sin * dy, ry = cy + sin * dx + cos * dy;
            (minX, maxX) = (Math.Min(minX, rx), Math.Max(maxX, rx));
            (minY, maxY) = (Math.Min(minY, ry), Math.Max(maxY, ry));
        }
        return new BoundingBox(
            (float)Math.Clamp(minX, 0, width), (float)Math.Clamp(minY, 0, height),
            (float)Math.Clamp(maxX, 0, width), (float)Math.Clamp(maxY, 0, height));
    }
}
