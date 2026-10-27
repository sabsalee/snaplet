using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.CompilerServices;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Both preview and export use source pixel coordinates and the same painter.</summary>
internal static class ImageRenderer
{
    internal static Rect RectOf(RectD r) => new(r.X, r.Y, r.Width, r.Height);
    internal static Brush BrushOf(ColorRgba c) => new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
    internal static Point P(PointD p) => new(p.X, p.Y);

    internal static IEnumerable<Annotation> LayerOrder(IEnumerable<Annotation> annotations)
    {
        var items=annotations.ToArray();return items.Where(a=>a.Kind==AnnotationKind.Image).Concat(items.Where(a=>a.Kind!=AnnotationKind.Image));
    }
    internal static void Paint(DrawingContext dc, BitmapSource source, IEnumerable<Annotation> annotations, Rect? canvasBounds=null,Guid? interactiveEffect=null)
    {
        var items=annotations.ToArray();
        var scene=new DrawingGroup();using(var initial=scene.Open())initial.DrawImage(source,new Rect(0,0,source.PixelWidth,source.PixelHeight));
        Drawing underlay=scene;Drawing all=scene;
        int context=17;
        foreach(var a in LayerOrder(items).Where(a=>a.Kind!=AnnotationKind.Spotlight)) {
            var layer=new DrawingGroup();using(var painter=layer.Open())PaintAnnotation(painter,source,a,underlay,context,a.Id==interactiveEffect);
            var next=new DrawingGroup();next.Children.Add(all);next.Children.Add(layer);all=next;
            if(a.Kind!=AnnotationKind.Magnifier) {var sampling=new DrawingGroup();sampling.Children.Add(underlay);sampling.Children.Add(layer);underlay=sampling;}
            context=HashCode.Combine(context,a);
        }
        dc.DrawDrawing(all);
        var canvas=canvasBounds ?? new Rect(0,0,source.PixelWidth,source.PixelHeight);
        // The caller supplies the export/preview crop through a clip; fallback includes expanded image objects.
        if(canvasBounds is null)foreach(var a in items)canvas.Union(BoundsOf(a));
        AdvancedDrawing.DrawSpotlights(dc,canvas,items.Where(a=>a.Kind==AnnotationKind.Spotlight));
    }

    internal static void PaintAnnotation(DrawingContext dc, BitmapSource source, Annotation a, Drawing? underlay=null, int context=0,bool interactiveEffect=false)
    {
        var brush = BrushOf(a.Color);
        var pen = new Pen(brush, a.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var rect = RectOf(a.Bounds);
        dc.PushOpacity(a.Appearance.Opacity);
        switch (a.Kind)
        {
            case AnnotationKind.Line:
            case AnnotationKind.Arrow:
                if (a.Kind == AnnotationKind.Arrow && a.Appearance.ArrowHead is ArrowHead.Filled or ArrowHead.Double)
                {
                    dc.DrawGeometry(brush, null, ArrowGeometry(a));
                    break;
                }
                dc.DrawGeometry(null, pen, ArrowStrokeGeometry(a));
                break;
            case AnnotationKind.Rectangle:
            case AnnotationKind.Ellipse:
                Brush? fill=null;
                if(a.Appearance.Fill!=ShapeFill.Outline) {
                    var color=a.Color;fill=BrushOf(color with { A=(byte)(a.Appearance.Fill==ShapeFill.Translucent ? color.A*.3 : color.A) });
                }
                var outline = ShapeGeometry(a);
                var hatchBounds=outline.Bounds;
                if (a.Appearance.Fill == ShapeFill.Hatched)
                {
                    dc.PushClip(outline);
                    var hatch = new Pen(brush, Math.Max(1, a.Thickness * .35)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    var gap = Math.Max(7, a.Thickness * 2.5);
                    // The source-coordinate pattern remains stable while dragging a shape.
                    double first = Math.Floor((hatchBounds.Left + hatchBounds.Top) / gap) * gap;
                    for (double k = first; k < hatchBounds.Right + hatchBounds.Bottom; k += gap)
                        dc.DrawLine(hatch, new(k - hatchBounds.Bottom, hatchBounds.Bottom), new(k - hatchBounds.Top, hatchBounds.Top));
                    dc.Pop();
                    fill = null;
                }
                dc.DrawGeometry(fill, pen, outline);
                break;
            case AnnotationKind.Pen:
                if(a.Points is {Count:1} dot)dc.DrawEllipse(brush,null,P(dot[0]),a.Thickness/2,a.Thickness/2);
                if (a.Points is { Count: > 1 } points)
                {
                    dc.DrawGeometry(null, pen, PenGeometry(a));
                }
                break;
            case AnnotationKind.Text:
                var body=TextBounds(a);var bubble=a.Appearance.Bubble;
                if(bubble) {
                    var bubbleRadius=Math.Min(a.Appearance.CornerRadius,Math.Min(body.Width,body.Height)/2);
                    var bodyGeometry = new RectangleGeometry(body, bubbleRadius, bubbleRadius);
                    dc.DrawGeometry(brush, null, a.Appearance.ShowTail
                        ? new CombinedGeometry(GeometryCombineMode.Union, bodyGeometry, TailGeometry(a)) : bodyGeometry);
                }
                var label=TextLayout(a,bubble?Contrast(a.Color):brush);
                dc.DrawText(label,new(body.X+(bubble?10:0),body.Y+(bubble?8:0)));
                break;
            case AnnotationKind.Step:
                var radius=AnnotationGeometry.CounterRadius(a);
                var circle = new EllipseGeometry(P(a.Start), radius, radius);
                dc.DrawGeometry(brush, null, a.Appearance.ShowTail
                    ? new CombinedGeometry(GeometryCombineMode.Union, circle, TailGeometry(a)) : circle);
                var text=new FormattedText(a.Text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI Semibold"),radius*1.2,Contrast(a.Color),1);
                dc.DrawText(text,new(a.Start.X-text.Width/2,a.Start.Y-text.Height/2));
                break;
            case AnnotationKind.Highlight:
                AdvancedDrawing.DrawHighlight(dc,a);break;
            case AnnotationKind.Image:
                if(a.Image!=null)dc.DrawImage(BitmapOf(a.Image),rect);break;
            case AnnotationKind.Magnifier:
                if(underlay is null) {var original=new DrawingGroup();using(var painter=original.Open())painter.DrawImage(source,new Rect(0,0,source.PixelWidth,source.PixelHeight));underlay=original;}
                AdvancedDrawing.DrawMagnifier(dc,a,underlay);break;
            case AnnotationKind.Spotlight:
                AdvancedDrawing.DrawSpotlights(dc,new Rect(0,0,source.PixelWidth,source.PixelHeight),new[]{a});break;
            case AnnotationKind.Solid: dc.DrawRectangle(brush, null, rect); break;
            case AnnotationKind.Pixelate:
            case AnnotationKind.Blur:
            case AnnotationKind.Erase:
                DrawEffect(dc, source, a, rect,underlay,context,interactiveEffect); break;
        }
        dc.Pop();
    }

    private static readonly ConditionalWeakTable<RasterImage,BitmapSource> imageAssets=new();
    internal static BitmapSource BitmapOf(RasterImage image)=>imageAssets.GetValue(image,data=>{
        var bitmap=BitmapSource.Create(data.Width,data.Height,96,96,PixelFormats.Bgra32,null,data.CopyPixels(),data.Width*4);bitmap.Freeze();return bitmap;
    });
    internal static RasterImage RasterOf(BitmapSource image)
    {
        var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
        var bytes=new byte[checked(image.PixelWidth*image.PixelHeight*4)];converted.CopyPixels(bytes,image.PixelWidth*4,0);
        return new(image.PixelWidth,image.PixelHeight,bytes);
    }
    internal static Geometry ArrowGeometry(Annotation a) => AnnotationPaths.Arrow(a);
    internal static Geometry ShapeGeometry(Annotation a)
    {
        var body=AnnotationPaths.Shape(a);
        return a.Appearance.ShapeTail ? new CombinedGeometry(GeometryCombineMode.Union,body,TailGeometry(a)) : body;
    }
    internal static PointD TailTip(Annotation a)
    {
        if(a.Kind==AnnotationKind.Step)return AnnotationGeometry.CounterTip(a);
        var body=BoundsOf(a);
        var target=a.Tail ?? new(body.X+body.Width*.35,body.Bottom+28);
        return a.Kind is AnnotationKind.Text or AnnotationKind.Rectangle
            ?AnnotationGeometry.CalloutTip(new(body.X,body.Y,body.Width,body.Height),target):target;
    }
    internal static Geometry PenGeometry(Annotation a) => AnnotationPaths.Pen(a.Points ?? new[] { a.Start, a.End });

    internal static Geometry ArrowStrokeGeometry(Annotation a)
    {
        var curve = new StreamGeometry();
        using (var g = curve.Open())
        {
            g.BeginFigure(P(a.Start), false, false);
            g.QuadraticBezierTo(P(a.Control ?? AnnotationGeometry.Midpoint(a.Start, a.End)), P(a.End), true, true);
        }
        if (a.Kind != AnnotationKind.Arrow) return curve;
        var from = a.Control ?? a.Start;
        if (AnnotationGeometry.Distance(from, a.End) < .001) from = a.Start;
        var result = new GeometryGroup(); result.Children.Add(curve);
        result.Children.Add(ArrowTip(a.End, from, a.Thickness, a.Appearance.ArrowHead));
        return result;
    }

    private static Geometry ArrowTip(PointD tip,PointD from,double thickness,ArrowHead style)
    {
        var tangent = P(tip) - P(from);
        if (tangent.LengthSquared < .000001) tangent = new(1, 0);
        tangent.Normalize(); var normal = new Vector(-tangent.Y, tangent.X);
        var length = Math.Max(12, thickness * (style == ArrowHead.Simple ? 3 : 5));
        var spread = length * (style == ArrowHead.Simple ? .36 : .52);
        var back = P(tip) - tangent * length;
        var left = back + normal * spread; var right = back - normal * spread;
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(left, false, false);
            if (style == ArrowHead.Open)
            {
                g.QuadraticBezierTo(P(tip) - tangent * (length * .4) + normal * (spread * .85), P(tip), true, true);
                g.QuadraticBezierTo(P(tip) - tangent * (length * .4) - normal * (spread * .85), right, true, true);
            }
            else { g.LineTo(P(tip), true, true); g.LineTo(right, true, true); }
        }
        shape.Freeze(); return shape;
    }
    internal static FormattedText TextLayout(Annotation a,Brush? ink=null)
    {
        var result=new FormattedText(string.IsNullOrEmpty(a.Text)?" ":a.Text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),Math.Max(14,a.Thickness*5),ink ?? Brushes.White,1);
        var width=Math.Abs(a.End.X-a.Start.X);
        result.MaxTextWidth=Math.Max(20,width>2?width-(a.Appearance.Bubble?20:0):240);
        return result;
    }
    internal static Rect TextBounds(Annotation a)
    {
        var text=TextLayout(a);var padding=a.Appearance.Bubble?20:0;
        return new(a.Start.X,a.Start.Y,Math.Max(Math.Max(24,padding+20),Math.Abs(a.End.X-a.Start.X)>2?Math.Abs(a.End.X-a.Start.X):text.Width+padding),
            Math.Max(Math.Abs(a.End.Y-a.Start.Y),text.Height+(a.Appearance.Bubble?16:0)));
    }
    internal static Rect BoundsOf(Annotation a)
    {
        if(a.Kind==AnnotationKind.Text)return TextBounds(a);
        if(a.Kind==AnnotationKind.Step) {var r=AnnotationGeometry.CounterRadius(a);return new(a.Start.X-r,a.Start.Y-r,r*2,r*2);}
        return RectOf(a.Bounds);
    }
    internal static Brush Contrast(ColorRgba c) => .2126*c.R+.7152*c.G+.0722*c.B>155?Brushes.Black:Brushes.White;
    internal static Geometry TailGeometry(Annotation a)
    {
        if (!AnnotationGeometry.HasTail(a)) return Geometry.Empty;
        if (a.Kind == AnnotationKind.Text && a.Appearance.Bubble)
        {
            var b = TextBounds(a);
            return AnnotationPaths.CalloutTail(b, TailTip(a),a.Appearance.TailWidth,a.Appearance.CornerRadius);
        }
        if(a.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse) {
            var body=RectOf(a.Bounds);
            return a.Kind==AnnotationKind.Ellipse ? AnnotationPaths.EllipseTail(body,TailTip(a),a.Appearance.TailWidth) : AnnotationPaths.CalloutTail(body,TailTip(a),a.Appearance.TailWidth,a.Appearance.CornerRadius);
        }
        return a.Kind == AnnotationKind.Step ? AnnotationPaths.CounterTail(a) : Geometry.Empty;
    }

    private static readonly ConditionalWeakTable<BitmapSource, Dictionary<(AnnotationKind, Int32Rect, double, int), BitmapSource>> effectCaches = new();
    private static void DrawEffect(DrawingContext dc, BitmapSource source, Annotation a, Rect rect,Drawing? underlay,int context,bool interactive=false)
    {
        if(interactive){DrawInteractiveEffect(dc,source,a,rect,underlay);return;}
        var bounds=new Int32Rect((int)Math.Floor(rect.X),(int)Math.Floor(rect.Y),Math.Max(0,(int)Math.Ceiling(rect.Right)-(int)Math.Floor(rect.X)),Math.Max(0,(int)Math.Ceiling(rect.Bottom)-(int)Math.Floor(rect.Y)));
        if (bounds.Width < 1 || bounds.Height < 1) return;
        var cache = effectCaches.GetOrCreateValue(source);
        var key = (a.Kind, bounds, a.Thickness,context);
        if (cache.TryGetValue(key, out var cached)) { dc.DrawImage(cached, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height)); return; }
        // Include the surrounding ring for Erase; sample the actual preceding layers, including pasted images.
        var padding=a.Kind==AnnotationKind.Erase?3:0;
        var observed=underlay?.Bounds ?? new Rect(0,0,source.PixelWidth,source.PixelHeight);
        var left=Math.Min(bounds.X,Math.Max(bounds.X-padding,(int)Math.Floor(observed.Left)));
        var top=Math.Min(bounds.Y,Math.Max(bounds.Y-padding,(int)Math.Floor(observed.Top)));
        var right=Math.Max(bounds.X+bounds.Width,Math.Min(bounds.X+bounds.Width+padding,(int)Math.Ceiling(observed.Right)));
        var bottom=Math.Max(bounds.Y+bounds.Height,Math.Min(bounds.Y+bounds.Height+padding,(int)Math.Ceiling(observed.Bottom)));
        var expanded=new Int32Rect(left,top,right-left,bottom-top);
        var offsetX=bounds.X-expanded.X;var offsetY=bounds.Y-expanded.Y;
        if((long)expanded.Width*expanded.Height>64_000_000)throw new InvalidOperationException("효과 영역이 너무 큽니다.");
        var visual=new DrawingVisual();using(var painter=visual.RenderOpen()) {
            painter.PushTransform(new TranslateTransform(-expanded.X,-expanded.Y));
            if(underlay!=null)painter.DrawDrawing(underlay);else painter.DrawImage(source,new Rect(0,0,source.PixelWidth,source.PixelHeight));
            painter.Pop();
        }
        var snapshot=new RenderTargetBitmap(expanded.Width,expanded.Height,96,96,PixelFormats.Pbgra32);snapshot.Render(visual);
        var converted=new FormatConvertedBitmap(snapshot,PixelFormats.Bgra32,null,0);
        var full=new byte[checked(expanded.Width*expanded.Height*4)];converted.CopyPixels(full,expanded.Width*4,0);
        if(a.Kind==AnnotationKind.Erase)PixelEffects.Erase(full,expanded.Width,expanded.Height,new(offsetX,offsetY,bounds.Width,bounds.Height));
        int stride=checked(bounds.Width*4);var pixels=new byte[checked(stride*bounds.Height)];
        for(int y=0;y<bounds.Height;y++)Array.Copy(full,((y+offsetY)*expanded.Width+offsetX)*4,pixels,y*stride,stride);
        var region = new RectD(0, 0, bounds.Width, bounds.Height);
        if (a.Kind == AnnotationKind.Blur) PixelEffects.Blur(pixels, bounds.Width, bounds.Height, region, Math.Clamp((int)(a.Thickness * 2), 3, 60));
        else if(a.Kind==AnnotationKind.Pixelate)PixelEffects.Pixelate(pixels, bounds.Width, bounds.Height, region, Math.Clamp((int)(a.Thickness * 3), 5, 4096));
        var result = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        if (cache.Count >= 24 || cache.Values.Sum(b => (long)b.PixelWidth * b.PixelHeight) + (long)bounds.Width * bounds.Height > 4_000_000) cache.Clear();
        cache[key] = result;
        dc.DrawImage(result, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    // Bound live effect processing independently of capture size; export keeps the exact full-resolution path.
    private static void DrawInteractiveEffect(DrawingContext dc,BitmapSource source,Annotation a,Rect rect,Drawing? underlay)
    {
        if(rect.Width<1 || rect.Height<1)return;
        var scale=Math.Min(1,Math.Sqrt(160_000/(rect.Width*rect.Height)));
        var width=Math.Max(1,(int)Math.Ceiling(rect.Width*scale));var height=Math.Max(1,(int)Math.Ceiling(rect.Height*scale));
        var ring=a.Kind==AnnotationKind.Erase?3:0;
        var visual=new DrawingVisual();using(var painter=visual.RenderOpen()) {
            painter.PushTransform(new TranslateTransform(ring,ring));painter.PushTransform(new ScaleTransform(scale,scale));
            painter.PushTransform(new TranslateTransform(-rect.X,-rect.Y));
            if(underlay!=null)painter.DrawDrawing(underlay);else painter.DrawImage(source,new Rect(0,0,source.PixelWidth,source.PixelHeight));
            painter.Pop();painter.Pop();painter.Pop();
        }
        var w=width+ring*2;var h=height+ring*2;
        var snapshot=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);snapshot.Render(visual);
        var converted=new FormatConvertedBitmap(snapshot,PixelFormats.Bgra32,null,0);var bytes=new byte[w*h*4];converted.CopyPixels(bytes,w*4,0);
        var region=new RectD(ring,ring,width,height);
        if(a.Kind==AnnotationKind.Erase)PixelEffects.Erase(bytes,w,h,region);
        else if(a.Kind==AnnotationKind.Blur)PixelEffects.Blur(bytes,w,h,region,Math.Clamp((int)Math.Round(Math.Clamp(a.Thickness*2,3,60)*scale),1,60));
        else PixelEffects.Pixelate(bytes,w,h,region,Math.Clamp((int)Math.Round(Math.Clamp(a.Thickness*3,5,4096)*scale),1,4096));
        var bitmap=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,bytes,w*4);bitmap.Freeze();
        BitmapSource result=ring==0?bitmap:new CroppedBitmap(bitmap,new Int32Rect(ring,ring,width,height));
        dc.DrawImage(result,rect);
    }

    internal static BitmapSource FlattenWhite(BitmapSource bitmap)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight)); dc.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight)); }
        var flat = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32); flat.Render(visual); flat.Freeze(); return flat;
    }

    private static Int32Rect Int32RectFrom(Rect r, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(r.Left), 0, width), top = Math.Clamp((int)Math.Floor(r.Top), 0, height);
        int right = Math.Clamp((int)Math.Ceiling(r.Right), left, width), bottom = Math.Clamp((int)Math.Ceiling(r.Bottom), top, height);
        return new(left, top, right - left, bottom - top);
    }

    internal static BitmapSource Render(BitmapSource source, EditableDocument document)
    {
        var bounds=document.Crop;
        var crop=new Int32Rect((int)Math.Floor(bounds.X),(int)Math.Floor(bounds.Y),(int)Math.Ceiling(bounds.Right)-(int)Math.Floor(bounds.X),(int)Math.Ceiling(bounds.Bottom)-(int)Math.Floor(bounds.Y));
        if (crop.Width < 1 || crop.Height < 1) throw new InvalidOperationException("잘라낼 영역이 비어 있습니다.");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-crop.X, -crop.Y));
            Paint(dc, source, document.Annotations,RectOf(document.Crop));
            dc.Pop();
        }
        var output = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual); output.Freeze();
        return output;
    }
}
