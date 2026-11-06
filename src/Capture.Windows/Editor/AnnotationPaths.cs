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

    internal static Geometry Arrow(Annotation a) => RibbonArrow(a);

    private static Geometry RibbonArrow(Annotation a)
    {
        const int samples = 64;
        var distances = new double[samples + 1];
        for (int i = 1; i <= samples; i++)
            distances[i] = distances[i - 1] + (Curve(a, i / (double)samples) - Curve(a, (i - 1) / (double)samples)).Length;
        var length = distances[^1];
        if (length < .5) return new EllipseGeometry(P(a.Start), a.Thickness / 2, a.Thickness / 2);
        // Proportions are in units of a 100-long reference arrow; the head keeps its size and only the shaft stretches.
        var (headScale, tailScale) = RibbonScales(a.Thickness, length);
        double headLength = 48 * headScale, barbBack = 46.2 * headScale, headHalf = 22.1 * headScale;
        double innerHalf = 10.5 * headScale, neckHalf = 8.9 * headScale, tailHalf = 7.1 * tailScale;
        double TAt(double distance)
        {
            distance = Math.Clamp(distance, 0, length);
            int j = Math.Max(1, Array.FindIndex(distances, d => d >= distance));
            return ((j - 1) + (distance - distances[j - 1]) / Math.Max(.000001, distances[j] - distances[j - 1])) / samples;
        }
        Point Side(double distance, double half, double sign)
        {
            var t = TAt(distance); var direction = Tangent(a, t);
            return Curve(a, t) + new Vector(-direction.Y, direction.X) * (half * sign);
        }
        var vertices = new List<Point>(); var rounding = new List<double>();
        void Add(Point p, double radius = 0) { vertices.Add(p); rounding.Add(radius); }
        const int steps = 24;
        void Body(double sign, double from, double to, double fromHalf, double toHalf, bool towardHead)
        {
            for (int i = 0; i <= steps; i++)
            {
                var j = towardHead ? i : steps - i;
                Add(Side(from + (to - from) * j / steps, fromHalf + (toHalf - fromHalf) * j / steps, sign));
            }
        }
        // A round tail is a half circle that follows the path's start.
        void RoundCap(double radius, double sign, bool towardHead)
        {
            const int arc = 6;
            for (int i = towardHead ? 0 : arc - 1; towardHead ? i < arc : i >= 1; i += towardHead ? 1 : -1)
            {
                var angle = i * Math.PI / 2 / arc;
                Add(Side(radius * (1 - Math.Cos(angle)), radius * Math.Sin(angle), sign));
            }
        }
        // Proportions below are measured from reference output; each outline is drawn here from those ratios alone.
        switch (a.Appearance.RibbonStyle)
        {
            case RibbonStyle.Round:
            {
                double head = 36.5 * headScale, half = 20.5 * headScale, throat = 7 * headScale, tail = 6 * tailScale;
                void Flank(double sign, bool towardTip)
                {
                    var outline = new[] { (head, throat, .6), (head + 1.2 * headScale, half, 1.5), (head * .45, half * .6, 6) };
                    foreach (var (back, width, radius) in towardTip ? outline : Enumerable.Reverse(outline))
                        Add(Side(length - back, width, sign), radius * headScale);
                }
                RoundCap(tail, 1, true);
                Body(1, tail, length - head, tail, throat, true);
                Flank(1, true);
                Add(P(a.End), .8 * headScale);
                Flank(-1, false);
                Body(-1, tail, length - head, tail, throat, false);
                RoundCap(tail, -1, false);
                return RoundedPolygon(vertices, rounding);
            }
            case RibbonStyle.Chevron:
            {
                double notch = 16 * headScale, throat = 6 * headScale, tail = 6 * tailScale;
                RoundCap(tail, 1, true);
                Body(1, tail, length - notch, tail, throat, true);
                Add(Side(length - 29 * headScale, 17 * headScale, 1), .8 * headScale);
                Add(Side(length - 22 * headScale, 21.5 * headScale, 1), 1.2 * headScale);
                Add(P(a.End), .8 * headScale);
                Add(Side(length - 22 * headScale, 21.5 * headScale, -1), 1.2 * headScale);
                Add(Side(length - 29 * headScale, 17 * headScale, -1), .8 * headScale);
                Body(-1, tail, length - notch, tail, throat, false);
                RoundCap(tail, -1, false);
                return RoundedPolygon(vertices, rounding);
            }
            case RibbonStyle.Band:
            {
                double head = 22 * headScale, half = 15 * headScale, tail = 15 * tailScale;
                Add(Side(3.3 * tailScale, 0, 1));
                Add(Side(0, tail, 1), tailScale);
                Body(1, tailScale, length - head, tail, half, true);
                Add(P(a.End), .8 * headScale);
                Body(-1, tailScale, length - head, tail, half, false);
                Add(Side(0, tail, -1), tailScale);
                return RoundedPolygon(vertices, rounding);
            }
        }
        var neck = length - headLength - .9 * headScale;
        for (int i = 0; i <= steps; i++)
            Add(Side(neck * i / steps, tailHalf + (neckHalf - tailHalf) * i / steps, 1), i == 0 ? 1.6 * tailScale : 0);
        Add(Side(length - headLength, innerHalf, 1), .6 * headScale);
        Add(Side(length - barbBack, headHalf, 1), 1.5 * headScale);
        Add(P(a.End), .8 * headScale);
        Add(Side(length - barbBack, headHalf, -1), 1.5 * headScale);
        Add(Side(length - headLength, innerHalf, -1), .6 * headScale);
        for (int i = steps; i >= 0; i--)
            Add(Side(neck * i / steps, tailHalf + (neckHalf - tailHalf) * i / steps, -1), i == 0 ? 1.6 * tailScale : 0);
        return RoundedPolygon(vertices, rounding);
    }

    /// <summary>Size step of a ribbon arrow from its thickness, and the length rules: short arrows shrink as a whole, long ones thin the tail.</summary>
    internal static (double Head, double Tail) RibbonScales(double thickness, double length)
    {
        var s = .18 * thickness + .15;
        var threshold = 56 * s < 100 ? 200 : 112 * s;
        if (length < threshold) { var k = length / threshold; return (s * k, s * k); }
        if (length <= 520) return (s, s);
        var f = Math.Max(.8, 1 - (length - 520) / 1000);
        return (s / f, s * f);
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
        var horizontal=body.Width-2*corner;var vertical=body.Height-2*corner;
        var arc=corner*Math.PI/2;var perimeter=2*(horizontal+vertical)+4*arc;
        var starts=new[]{new Point(body.Left+corner,body.Top),new Point(body.Right,body.Top+corner),new Point(body.Right-corner,body.Bottom),new Point(body.Left,body.Bottom-corner)};
        var centers=new[]{new Point(body.Right-corner,body.Top+corner),new Point(body.Right-corner,body.Bottom-corner),new Point(body.Left+corner,body.Bottom-corner),new Point(body.Left+corner,body.Top+corner)};
        var directions=new[]{new Vector(1,0),new Vector(0,1),new Vector(-1,0),new Vector(0,-1)};
        (Point Point,Vector Tangent) Edge(double distance) {
            distance=(distance%perimeter+perimeter)%perimeter;
            for(int i=0;i<4;i++) {
                var line=i%2==0?horizontal:vertical;
                if(distance<line)return (starts[i]+directions[i]*distance,directions[i]);
                distance-=line;
                if(distance<arc) {
                    var angle=-Math.PI/2+i*Math.PI/2+distance/corner;
                    return (centers[i]+new Vector(Math.Cos(angle),Math.Sin(angle))*corner,new Vector(-Math.Sin(angle),Math.Cos(angle)));
                }
                distance-=arc;
            }
            return (starts[0],directions[0]);
        }
        // Follow the whole rounded perimeter so a base can straddle a corner,
        // rather than folding a tail over one arbitrarily chosen flat side.
        double best=0,bestDistance=double.MaxValue;
        for(int i=0;i<64;i++) {
            var at=perimeter*i/64;var d=(Edge(at).Point-tip).LengthSquared;
            if(d<bestDistance){best=at;bestDistance=d;}
        }
        var low=best-perimeter/64;var high=best+perimeter/64;
        for(int i=0;i<24;i++) {
            var first=low+(high-low)/3;var second=high-(high-low)/3;
            if((Edge(first).Point-tip).LengthSquared<(Edge(second).Point-tip).LengthSquared)high=second;else low=first;
        }
        var position=(low+high)/2;var (anchor,tangent)=Edge(position);
        var normal=Unit(tip-anchor);var half=Math.Min(width/2,Math.Min(body.Width,body.Height)*.3);
        var (a,ta)=Edge(position-half);var (b,tb)=Edge(position+half);
        a+=new Vector(-ta.Y,ta.X)*.15;b+=new Vector(-tb.Y,tb.X)*.15;
        var reach=Math.Max(1,(tip-anchor).Length);
        var tipRounding=Math.Min(.8,reach*.12);
        var tipLeft=tip-normal*tipRounding-tangent*tipRounding;
        var tipRight=tip-normal*tipRounding+tangent*tipRounding;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(a, true, true);
            // Tangent shoulders flow into tapered sides with a softly rounded tip.
            var sweep=Math.Min(half*.35,reach*.45);
            g.BezierTo(a+ta*sweep, tipLeft-normal*Math.Min(reach*.45,half*1.5)-tangent*half*.25,tipLeft,true,true);
            g.QuadraticBezierTo(tip+normal*tipRounding*.2,tipRight,true,true);
            g.BezierTo(tipRight-normal*Math.Min(reach*.45,half*1.5)+tangent*half*.25,b-tb*sweep,b,true,true);
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
