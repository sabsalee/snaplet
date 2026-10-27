namespace Capture.Core;

/// <summary>Geometry shared by rendering, hit testing and gesture handles, in source pixels.</summary>
public static class AnnotationGeometry
{
    public static PointD Midpoint(PointD a, PointD b) => new((a.X+b.X)/2,(a.Y+b.Y)/2);
    public static PointD CurvePoint(Annotation a, double t)
    {
        var c=a.Control ?? Midpoint(a.Start,a.End); var u=1-t;
        return new(u*u*a.Start.X+2*u*t*c.X+t*t*a.End.X,u*u*a.Start.Y+2*u*t*c.Y+t*t*a.End.Y);
    }
    // The middle handle lies ON the curve; control point itself is off the curve.
    public static Annotation BendThrough(Annotation a, PointD middle) => a with {
        Control=new(2*middle.X-(a.Start.X+a.End.X)/2,2*middle.Y-(a.Start.Y+a.End.Y)/2) };
    public static double Distance(PointD a, PointD b) => Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    public static double SegmentDistance(PointD p, PointD a, PointD b)
    {
        double dx=b.X-a.X,dy=b.Y-a.Y;
        var t=Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/Math.Max(1e-12,dx*dx+dy*dy),0,1);
        return Distance(p,new(a.X+t*dx,a.Y+t*dy));
    }
    public static double CurveDistance(Annotation a, PointD p)
    {
        double distance=double.MaxValue;var last=a.Start;
        for(int i=1;i<=48;i++) {var next=CurvePoint(a,i/48d);distance=Math.Min(distance,SegmentDistance(p,last,next));last=next;}
        return distance;
    }
    /// <summary>Keep the drag length while snapping to the closest 45 degree direction.</summary>
    public static PointD SnapAngle(PointD anchor, PointD point)
    {
        var length=Distance(anchor,point);
        var angle=Math.Round(Math.Atan2(point.Y-anchor.Y,point.X-anchor.X)/(Math.PI/4))*(Math.PI/4);
        return new(anchor.X+length*Math.Cos(angle),anchor.Y+length*Math.Sin(angle));
    }
    /// <summary>Align the closest edge/center pair, with tolerance in source pixels.</summary>
    public static (double Offset,double? Guide) SnapAxis(double min,double max,IEnumerable<double> targets,double tolerance)
    {
        double best=tolerance,offset=0;double? guide=null;
        foreach(var target in targets)foreach(var value in new[]{min,(min+max)/2,max}) {
            var delta=target-value;
            if(Math.Abs(delta)<=best){best=Math.Abs(delta);offset=delta;guide=target;}
        }
        return (offset,guide);
    }
    public static bool HasTail(Annotation a) => a.Kind switch {
        AnnotationKind.Rectangle or AnnotationKind.Ellipse => a.Appearance.ShapeTail,
        AnnotationKind.Step => a.Appearance.ShowTail,
        AnnotationKind.Text => a.Appearance.Bubble && a.Appearance.ShowTail,
        _ => false
    };
    public static double CounterRadius(Annotation a) => Math.Max(12,a.Thickness*4);
    /// <summary>Bound a callout's reach beyond its body, independently of the drag distance.</summary>
    public static PointD CalloutTip(RectD body,PointD target)
    {
        var edge=new PointD(Math.Clamp(target.X,body.X,body.Right),Math.Clamp(target.Y,body.Y,body.Bottom));
        var distance=Distance(edge,target);
        var maximum=Math.Clamp(Math.Min(body.Width,body.Height)*.6,24,48);
        if(distance<=maximum)return target;
        return new(edge.X+(target.X-edge.X)*maximum/distance,edge.Y+(target.Y-edge.Y)*maximum/distance);
    }
    public static PointD CounterTip(Annotation a)
    {
        var r=CounterRadius(a);var t=a.Tail ?? new(a.Start.X-r*1.03,a.Start.Y+r*1.03);
        var length=Distance(a.Start,t);
        if(length<.001)return new(a.Start.X-r*1.03,a.Start.Y+r*1.03);
        var clamped=Math.Clamp(length,r*1.15,r*1.55);
        return new(a.Start.X+(t.X-a.Start.X)*clamped/length,a.Start.Y+(t.Y-a.Start.Y)*clamped/length);
    }
}
