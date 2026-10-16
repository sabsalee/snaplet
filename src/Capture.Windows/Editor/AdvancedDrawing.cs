using System.Windows;
using System.Windows.Media;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Shared vector painting for the magnifier, marker and spotlight tools.</summary>
internal static class AdvancedDrawing
{
    internal static RectD SampleBounds(Annotation a)
    {
        if (a.SampleRegion is RectD sample && !sample.IsEmpty) return sample;
        var bounds = a.Bounds;
        double width = Math.Max(1, bounds.Width) / a.Appearance.Magnification;
        double height = Math.Max(1, bounds.Height) / a.Appearance.Magnification;
        return new(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
    }

    internal static void DrawMagnifier(DrawingContext dc, Annotation a, Drawing underlay)
    {
        var lens = ImageRenderer.RectOf(a.Bounds);
        if (lens.IsEmpty || lens.Width <= 0 || lens.Height <= 0) return;
        var sample = ImageRenderer.RectOf(SampleBounds(a));
        var ink = ImageRenderer.BrushOf(a.Color);
        var sourceGeometry = Frame(sample, a.Appearance.Elliptical);
        var lensGeometry = Frame(lens, a.Appearance.Elliptical);
        double thickness = Math.Max(1, a.Thickness);
        var sourcePen = new Pen(ink, Math.Max(1, thickness * .5)) { LineJoin = PenLineJoin.Round };
        var lensPen = new Pen(ink, thickness) { LineJoin = PenLineJoin.Round };
        Point sourceCenter = new(sample.X + sample.Width / 2, sample.Y + sample.Height / 2);
        Point lensCenter = new(lens.X + lens.Width / 2, lens.Y + lens.Height / 2);
        Vector direction = lensCenter - sourceCenter;
        if (direction.LengthSquared > .001)
        {
            var from = FrameEdge(sample, direction, a.Appearance.Elliptical);
            var to = FrameEdge(lens, -direction, a.Appearance.Elliptical);
            // Overlapping source/lens frames have no exposed connector segment.
            if (Vector.Multiply(to - from, direction) > 0)
                dc.DrawLine(new Pen(ink, Math.Max(1, thickness * .45)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, from, to);
        }
        dc.DrawGeometry(null, sourcePen, sourceGeometry);
        var image = new DrawingBrush(underlay)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = sample,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = lens,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        // A narrow white keyline keeps the border legible on both dark and light content.
        dc.DrawGeometry(Brushes.White, new Pen(Brushes.White, thickness + 2), lensGeometry);
        dc.DrawGeometry(image, lensPen, lensGeometry);
    }

    private static Geometry Frame(Rect bounds, bool elliptical) => elliptical
        ? new EllipseGeometry(bounds)
        : new RectangleGeometry(bounds, Math.Min(5, bounds.Width / 8), Math.Min(5, bounds.Height / 8));

    private static Point FrameEdge(Rect bounds, Vector direction, bool elliptical)
    {
        double halfWidth = Math.Max(.001, bounds.Width / 2), halfHeight = Math.Max(.001, bounds.Height / 2);
        double factor = elliptical
            ? 1 / Math.Sqrt(direction.X * direction.X / (halfWidth * halfWidth) + direction.Y * direction.Y / (halfHeight * halfHeight))
            : 1 / Math.Max(Math.Abs(direction.X) / halfWidth, Math.Abs(direction.Y) / halfHeight);
        return new(bounds.X + halfWidth + direction.X * factor, bounds.Y + halfHeight + direction.Y * factor);
    }

    internal static Geometry MarkerGeometry(Annotation a)
    {
        double width = Math.Max(1, a.Thickness * 5);
        if (a.Appearance.HighlightShape == MarkerShape.Rectangle)
        {
            var rectangle = new RectangleGeometry(ImageRenderer.RectOf(a.Bounds));
            rectangle.Freeze(); return rectangle;
        }
        IReadOnlyList<PointD> points = a.Appearance.HighlightShape == MarkerShape.Freehand && a.Points is { Count: > 0 } freehand
            ? freehand : new[] { a.Start, a.End };
        var cap = a.Appearance.MarkerCap;
        var pen = new Pen(Brushes.Black, width)
        {
            StartLineCap = cap == MarkerCap.Round ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = cap == MarkerCap.Round ? PenLineCap.Round : PenLineCap.Flat,
            LineJoin = PenLineJoin.Round
        };
        if (points.Count == 1 || points.All(p => AnnotationGeometry.Distance(p, points[0]) < .001))
        {
            var point = ImageRenderer.P(points[0]);
            Geometry dot = cap == MarkerCap.Round ? new EllipseGeometry(point, width / 2, width / 2)
                : new RectangleGeometry(new Rect(point.X - width / 2, point.Y - width / 2, width, width));
            dot.Freeze(); return dot;
        }
        var path = AnnotationPaths.Pen(points);
        Geometry body = path.GetWidenedPathGeometry(pen, .15, ToleranceType.Absolute);
        if (cap == MarkerCap.Sketch)
        {
            var first = ImageRenderer.P(points[0]); var last = ImageRenderer.P(points[^1]);
            Point next = ImageRenderer.P(points.First(p => AnnotationGeometry.Distance(p, points[0]) >= .001));
            Point previous = ImageRenderer.P(points.Last(p => AnnotationGeometry.Distance(p, points[^1]) >= .001));
            body = new CombinedGeometry(GeometryCombineMode.Union, body, SketchEnd(first, first - next, width));
            body = new CombinedGeometry(GeometryCombineMode.Union, body, SketchEnd(last, last - previous, width));
        }
        body.Freeze(); return body;
    }

    private static Geometry SketchEnd(Point point, Vector outward, double width)
    {
        outward.Normalize(); var normal = new Vector(-outward.Y, outward.X);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(point - outward * (width * .08) - normal * (width / 2), true, true);
            // Fixed nib irregularities remain stable when the annotation is moved or exported.
            double[] nib = [.04, .12, .065, .15, .075, .11, .025];
            for (int i = 0; i < nib.Length; i++)
                g.LineTo(point + normal * (width * (i / (double)(nib.Length - 1) - .5)) + outward * (width * nib[i]), true, false);
            g.LineTo(point - outward * (width * .08) + normal * (width / 2), true, false);
        }
        geometry.Freeze(); return geometry;
    }

    internal static void DrawHighlight(DrawingContext dc, Annotation a)
    {
        dc.PushOpacity(.32);
        dc.DrawGeometry(ImageRenderer.BrushOf(a.Color), null, MarkerGeometry(a));
        dc.Pop();
    }

    internal static void DrawSpotlights(DrawingContext dc, Rect canvas, IEnumerable<Annotation> annotations)
    {
        var spotlights = annotations.Where(a => a.Kind == AnnotationKind.Spotlight && !a.Bounds.IsEmpty).ToArray();
        if (spotlights.Length == 0 || canvas.IsEmpty) return;
        Geometry? holes = null;
        foreach (var a in spotlights)
        {
            var hole = Frame(ImageRenderer.RectOf(a.Bounds), a.Appearance.Elliptical);
            holes = holes is null ? hole : new CombinedGeometry(GeometryCombineMode.Union, holes, hole);
        }
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(canvas), holes!);
        var dim = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * spotlights.Max(a => a.Appearance.Dim * a.Appearance.Opacity)), 0, 0, 0));
        dim.Freeze();
        dc.DrawGeometry(dim, null, outside);
    }
}
