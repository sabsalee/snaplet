using System.Windows;
using System.Windows.Media;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Source-pixel vector paths shared by painting and selection. No device-dependent raster shapes.</summary>
internal static class AnnotationPaths
{
    private static Point P(PointD p) => new(p.X, p.Y);
    private static Point Offset(Point p, Vector v, double distance) => p + v * distance;
    private static Vector Unit(Vector v) => v.LengthSquared > .000001 ? v / v.Length : new(1, 0);
    private static Point Curve(Annotation a, double t) => P(AnnotationGeometry.CurvePoint(a, t));
    private static Vector Tangent(Annotation a, double t)
    {
        var c = P(a.Control ?? AnnotationGeometry.Midpoint(a.Start, a.End));
        var derivative = (c - P(a.Start)) * (1 - t) + (P(a.End) - c) * t;
        return Unit(derivative.LengthSquared > .000001 ? derivative : P(a.End) - P(a.Start));
    }

    internal static Geometry Arrow(Annotation a)
    {
        if (a.Appearance.ArrowHead == ArrowHead.Filled) return RibbonArrow(a);
        const int samples = 64;
        var distances = new double[samples + 1];
        for (int i = 1; i <= samples; i++) distances[i] = distances[i - 1] + (Curve(a, (double)i / samples) - Curve(a, (double)(i - 1) / samples)).Length;
        var length = distances[^1];
        if (length < .5) return new EllipseGeometry(P(a.Start), a.Thickness / 2, a.Thickness / 2);
        var doubleHead = a.Appearance.ArrowHead == ArrowHead.Double;
        // Broad filled arrows deliberately have a different silhouette from a stroked line arrow.
        var headLength = Math.Min(Math.Max(14, a.Thickness * 8.5), length * (doubleHead ? .34 : .43));
        var headWidth = Math.Min(a.Thickness * 3.1, headLength * .54);
        var shaftWidth = Math.Min(a.Thickness * 1.4, headWidth * .55);
        double AtDistance(double distance)
        {
            int i = Array.FindIndex(distances, x => x >= distance);
            if (i <= 0) return 0;
            return ((i - 1) + (distance - distances[i - 1]) / Math.Max(.000001, distances[i] - distances[i - 1])) / samples;
        }
        var startT = doubleHead ? AtDistance(headLength) : 0;
        var endT = AtDistance(length - headLength);
        var vertices = new List<Point>();
        var cornerRadii = new List<double>();
        void Add(Point p, double rounding) { vertices.Add(p); cornerRadii.Add(rounding); }
        var start = Curve(a, startT); var end = Curve(a, endT);
        var startNormal = new Vector(-Tangent(a, startT).Y, Tangent(a, startT).X);
        var endNormal = new Vector(-Tangent(a, endT).Y, Tangent(a, endT).X);
        if (doubleHead)
        {
            Add(P(a.Start), Math.Min(.6, a.Thickness * .14));
            Add(Offset(start, startNormal, headWidth), a.Thickness * .35);
        }
        // A subtly tapered, softly squared tail follows the same bend as the head.
        for (int i = 0; i <= 32; i++)
        {
            double t = startT + (endT - startT) * i / 32;
            var tangent = Tangent(a, t); var normal = new Vector(-tangent.Y, tangent.X);
            double width = shaftWidth * (doubleHead ? 1 : .68 + .32 * i / 32);
            Add(Offset(Curve(a, t), normal, width), i is 0 or 32 ? a.Thickness * .35 : 0);
        }
        Add(Offset(end, endNormal, headWidth), a.Thickness * .4);
        Add(P(a.End), Math.Min(.6, a.Thickness * .14));
        Add(Offset(end, endNormal, -headWidth), a.Thickness * .4);
        for (int i = 32; i >= 0; i--)
        {
            double t = startT + (endT - startT) * i / 32;
            var tangent = Tangent(a, t); var normal = new Vector(-tangent.Y, tangent.X);
            double width = shaftWidth * (doubleHead ? 1 : .68 + .32 * i / 32);
            Add(Offset(Curve(a, t), normal, -width), i is 0 or 32 ? a.Thickness * .35 : 0);
        }
        if (doubleHead) Add(Offset(start, startNormal, -headWidth), a.Thickness * .35);
        return RoundedPolygon(vertices, cornerRadii);
    }

    private static Geometry RibbonArrow(Annotation a)
    {
        const int samples = 64;
        var distances = new double[samples + 1];
        for (int i = 1; i <= samples; i++)
            distances[i] = distances[i - 1] + (Curve(a, i / (double)samples) - Curve(a, (i - 1) / (double)samples)).Length;
        var length = distances[^1];
        if (length < .5) return new EllipseGeometry(P(a.Start), a.Thickness / 2, a.Thickness / 2);
        // A broad ribbon uses a short wedge nose and a softly concave tail.
        var noseLength = Math.Min(a.Thickness * 4, length * .3);
        var halfWidth = Math.Min(a.Thickness * 3, length * .3);
        var neckDistance = length - noseLength;
        int j = Array.FindIndex(distances, d => d >= neckDistance);
        var neckT = ((j - 1) + (neckDistance - distances[j - 1]) / Math.Max(.000001, distances[j] - distances[j - 1])) / samples;
        var vertices = new List<Point>(); var rounding = new List<double>();
        void Add(Point p, double radius = 0) { vertices.Add(p); rounding.Add(radius); }
        for (int i = 0; i <= 40; i++)
        {
            double t = neckT * i / 40;
            var direction = Tangent(a, t); var normal = new Vector(-direction.Y, direction.X);
            Add(Curve(a, t) + normal * (halfWidth * (.85 + .15 * i / 40)), i is 0 or 40 ? a.Thickness * .35 : 0);
        }
        Add(P(a.End), Math.Min(.8, a.Thickness * .18));
        for (int i = 40; i >= 0; i--)
        {
            double t = neckT * i / 40;
            var direction = Tangent(a, t); var normal = new Vector(-direction.Y, direction.X);
            Add(Curve(a, t) - normal * (halfWidth * (.85 + .15 * i / 40)), i is 0 or 40 ? a.Thickness * .35 : 0);
        }
        var tailDirection = Tangent(a, 0); var tailNormal = new Vector(-tailDirection.Y, tailDirection.X);
        var indent = Math.Min(a.Thickness * .8, length * .08);
        // A shallow parabolic cut gives the tail a soft concavity rather than a sharp V notch.
        for (int i = 1; i < 16; i++)
        {
            double across = -1 + i / 8d;
            Add(P(a.Start) + tailNormal * (halfWidth * .85 * across) + tailDirection * (indent * (1 - across * across)));
        }
        return RoundedPolygon(vertices, rounding);
    }

    private static Geometry RoundedPolygon(IReadOnlyList<Point> vertices, IReadOnlyList<double> radii)
    {
        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var g = geo.Open())
        {
            Point Before(int i)
            {
                var p = vertices[i]; var previous = vertices[(i + vertices.Count - 1) % vertices.Count];
                return Offset(p, Unit(previous - p), Math.Min(radii[i], (previous - p).Length * .35));
            }
            Point After(int i)
            {
                var p = vertices[i]; var next = vertices[(i + 1) % vertices.Count];
                return Offset(p, Unit(next - p), Math.Min(radii[i], (next - p).Length * .35));
            }
            g.BeginFigure(After(0), true, true);
            for (int i = 1; i <= vertices.Count; i++)
            {
                int j = i % vertices.Count;
                g.LineTo(Before(j), true, true);
                g.QuadraticBezierTo(vertices[j], After(j), true, true);
            }
        }
        geo.Freeze(); return geo;
    }

    internal static Geometry Pen(IReadOnlyList<PointD> points)
    {
        var geo = new StreamGeometry();
        if (points.Count < 2) return geo;
        using (var g = geo.Open())
        {
            g.BeginFigure(P(points[0]), false, false);
            for (int i = 0; i < points.Count - 1; i++)
            {
                var p0 = P(points[Math.Max(0, i - 1)]); var p1 = P(points[i]);
                var p2 = P(points[i + 1]); var p3 = P(points[Math.Min(points.Count - 1, i + 2)]);
                // Bounded Catmull–Rom tangents avoid long overshoots near a pause or sharp turn.
                var chord = (p2 - p1).Length;
                Vector Limit(Vector v) => v.Length > chord * .45 ? Unit(v) * chord * .45 : v;
                g.BezierTo(p1 + Limit((p2 - p0) / 6), p2 - Limit((p3 - p1) / 6), p2, true, true);
            }
        }
        geo.Freeze(); return geo;
    }

    internal static Geometry CounterTail(Annotation a)
    {
        var r = AnnotationGeometry.CounterRadius(a); var center = P(a.Start); var tip = P(AnnotationGeometry.CounterTip(a));
        var distance = (tip - center).Length; var n = Unit(tip - center); var side = new Vector(-n.Y, n.X);
        var angle = Math.Acos(Math.Clamp(r / Math.Max(r, distance), 0, 1));
        var left = center + n * (r * Math.Cos(angle)) - side * (r * Math.Sin(angle));
        var right = center + n * (r * Math.Cos(angle)) + side * (r * Math.Sin(angle));
        var rounding = Math.Max(.4, r * .055);
        var tipLeft = tip - n * rounding - side * rounding;
        var tipRight = tip - n * rounding + side * rounding;
        var leftTangent = n * Math.Sin(angle) + side * Math.Cos(angle);
        var rightTangent = n * Math.Sin(angle) - side * Math.Cos(angle);
        var noseHandle = Math.Min(r * .25, Math.Max(rounding * 1.5, (distance - r) * .4));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(left, true, true);
            g.BezierTo(left + leftTangent * (r * .28), tipLeft - n * noseHandle, tipLeft, true, true);
            // Two quarter-arcs form a round nose with matching incoming/outgoing tangents.
            // The tip handle lies at the actual silhouette extremity, not outside the paint.
            const double kappa = .55228475;
            g.BezierTo(tipLeft + n * (rounding * kappa), tip - side * (rounding * kappa), tip, true, true);
            g.BezierTo(tip + side * (rounding * kappa), tipRight + n * (rounding * kappa), tipRight, true, true);
            g.BezierTo(tipRight - n * noseHandle, right + rightTangent * (r * .28), right, true, true);
        }
        geometry.Freeze(); return geometry;
    }

    internal static Geometry CalloutTail(Rect body, PointD target, double width=24, double radius=8)
    {
        var tip = P(target);
        if (body.Width<=0 || body.Height<=0 || body.Contains(tip)) return Geometry.Empty;
        var corner=Math.Min(radius,Math.Min(body.Width,body.Height)*.5);
        var center = new Point(body.X + body.Width / 2, body.Y + body.Height / 2);
        var dx = tip.X - center.X; var dy = tip.Y - center.Y;
        Vector normal, tangent; Point anchor; double half;
        if (Math.Abs(dx) / body.Width > Math.Abs(dy) / body.Height)
        {
            normal = new(dx < 0 ? -1 : 1, 0); tangent = new(0, 1);
            half = Math.Min(width/2, body.Height*.3);
            var x = dx < 0 ? body.Left : body.Right;
            anchor = new(x, Math.Clamp(center.Y + dy * (x - center.X) / dx, body.Top + half, body.Bottom - half));
        }
        else
        {
            normal = new(0, dy < 0 ? -1 : 1); tangent = new(1, 0);
            half = Math.Min(width/2, body.Width*.3);
            var y = dy < 0 ? body.Top : body.Bottom;
            anchor = new(Math.Clamp(center.X + dx * (y - center.Y) / dy, body.Left + half, body.Right - half), y);
        }
        Point Root(Point point) {
            var along=normal.X!=0?point.Y:point.X;
            var low=normal.X!=0?body.Top:body.Left;var high=normal.X!=0?body.Bottom:body.Right;
            var offset=Math.Max(0,Math.Max(low+corner-along,along-(high-corner)));
            var inset=corner-Math.Sqrt(Math.Max(0,corner*corner-offset*offset));
            return point-normal*(inset+.15);
        }
        var a = Root(anchor - tangent * half); var b = Root(anchor + tangent * half);
        double reach = Math.Max(1, Vector.Multiply(tip - anchor, normal));
        var tipRounding = Math.Min(.8, reach * .12);
        var tipLeft = tip - normal * tipRounding - tangent * tipRounding;
        var tipRight = tip - normal * tipRounding + tangent * tipRounding;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(a, true, true);
            // Tangent shoulders flow into tapered sides with a softly rounded tip.
            var sweep=Math.Min(half*.35,reach*.45);
            g.BezierTo(a+tangent*sweep, tipLeft-normal*Math.Min(reach*.45,half*1.5)-tangent*half*.25,tipLeft,true,true);
            g.QuadraticBezierTo(tip+normal*tipRounding*.2,tipRight,true,true);
            g.BezierTo(tipRight-normal*Math.Min(reach*.45,half*1.5)+tangent*half*.25,b-tangent*sweep,b,true,true);
        }
        geo.Freeze(); return geo;
    }

    internal static Geometry EllipseTail(Rect body,PointD target,double width=24)
    {
        if(body.Width<=0 || body.Height<=0)return Geometry.Empty;
        var center=new Point(body.X+body.Width/2,body.Y+body.Height/2);
        var rx=body.Width/2;var ry=body.Height/2;var tip=P(target);
        var normalized=new Vector((tip.X-center.X)/rx,(tip.Y-center.Y)/ry);
        if(normalized.Length<=1)return Geometry.Empty;
        var angle=Math.Atan2(normalized.Y,normalized.X);
        var spread=Math.Clamp(width/2/Math.Min(rx,ry),.08,.7);
        Point Edge(double theta)=>new(center.X+rx*Math.Cos(theta),center.Y+ry*Math.Sin(theta));
        Vector TangentAt(double theta)=>Unit(new(-rx*Math.Sin(theta),ry*Math.Cos(theta)));
        var left=Edge(angle-spread);var right=Edge(angle+spread);
        var n=Unit(tip-center);var side=new Vector(-n.Y,n.X);
        var reach=(tip-Edge(angle)).Length;
        var shoulder=Math.Min(Math.Min(rx,ry)*.3,reach*.25);
        var rounding=Math.Min(.8,reach*.12);
        var tipLeft=tip-n*rounding-side*rounding;var tipRight=tip-n*rounding+side*rounding;
        var geo=new StreamGeometry();
        using(var g=geo.Open()) {
            g.BeginFigure(left,true,true);
            g.BezierTo(left+TangentAt(angle-spread)*shoulder,tipLeft-n*shoulder,tipLeft,true,true);
            g.QuadraticBezierTo(tip+n*rounding*.2,tipRight,true,true);
            g.BezierTo(tipRight-n*shoulder,right-TangentAt(angle+spread)*shoulder,right,true,true);
        }
        geo.Freeze();return geo;
    }

    internal static Geometry Shape(Annotation a)
    {
        var bounds = ImageRenderer.RectOf(a.Bounds);
        if (!a.Appearance.Sketch)
            return a.Kind == AnnotationKind.Ellipse ? new EllipseGeometry(bounds) : new RectangleGeometry(bounds, Math.Min(a.Appearance.CornerRadius, Math.Min(bounds.Width, bounds.Height) * .5), Math.Min(a.Appearance.CornerRadius, Math.Min(bounds.Width, bounds.Height) * .5));
        // Stable organic contours: moving/resizing a shape never makes its texture flicker.
        var seed = a.Id.ToByteArray();
        double Bias(int i) => (seed[i % seed.Length] / 255.0 - .5);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            if (a.Kind == AnnotationKind.Ellipse)
            {
                double rx = bounds.Width / 2, ry = bounds.Height / 2;
                var c = new Point(bounds.X + rx, bounds.Y + ry);
                const double k = .55228475;
                var top = new Point(c.X + Bias(0) * Math.Min(3, rx * .08), bounds.Top);
                var right = new Point(bounds.Right, c.Y + Bias(1) * Math.Min(3, ry * .08));
                var bottom = new Point(c.X + Bias(2) * Math.Min(3, rx * .08), bounds.Bottom);
                var left = new Point(bounds.Left, c.Y + Bias(3) * Math.Min(3, ry * .08));
                g.BeginFigure(top, true, true);
                g.BezierTo(new(c.X + rx * k, bounds.Top), new(bounds.Right, c.Y - ry * k), right, true, true);
                g.BezierTo(new(bounds.Right, c.Y + ry * k), new(c.X + rx * k, bounds.Bottom), bottom, true, true);
                g.BezierTo(new(c.X - rx * k, bounds.Bottom), new(bounds.Left, c.Y + ry * k), left, true, true);
                g.BezierTo(new(bounds.Left, c.Y - ry * k), new(c.X - rx * k, bounds.Top), top, true, true);
            }
            else
            {
                double r = Math.Min(10, Math.Min(bounds.Width, bounds.Height) * .18);
                double bowX = Math.Min(3, bounds.Width * .025), bowY = Math.Min(3, bounds.Height * .025);
                g.BeginFigure(new(bounds.Left + r, bounds.Top), true, true);
                g.QuadraticBezierTo(new(bounds.X + bounds.Width / 2, bounds.Top + Bias(0) * bowY), new(bounds.Right - r, bounds.Top), true, true);
                g.QuadraticBezierTo(bounds.TopRight, new(bounds.Right, bounds.Top + r), true, true);
                g.QuadraticBezierTo(new(bounds.Right + Bias(1) * bowX, bounds.Y + bounds.Height / 2), new(bounds.Right, bounds.Bottom - r), true, true);
                g.QuadraticBezierTo(bounds.BottomRight, new(bounds.Right - r, bounds.Bottom), true, true);
                g.QuadraticBezierTo(new(bounds.X + bounds.Width / 2, bounds.Bottom + Bias(2) * bowY), new(bounds.Left + r, bounds.Bottom), true, true);
                g.QuadraticBezierTo(bounds.BottomLeft, new(bounds.Left, bounds.Bottom - r), true, true);
                g.QuadraticBezierTo(new(bounds.Left + Bias(3) * bowX, bounds.Y + bounds.Height / 2), new(bounds.Left, bounds.Top + r), true, true);
                g.QuadraticBezierTo(bounds.TopLeft, new(bounds.Left + r, bounds.Top), true, true);
            }
        }
        geo.Freeze(); return geo;
    }
}
