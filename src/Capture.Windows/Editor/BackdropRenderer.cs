using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>
/// Composes an exported image onto its backdrop: gradient/solid/transparent fill, padding, rounded corners and a soft shadow.
/// The same brushes and geometry drive the editor's live preview so what you see is what you copy.
/// </summary>
internal static class BackdropRenderer
{
    /// <summary>Eight gradient presets (top-left → bottom-right). Original palette, not taken from any other app.</summary>
    internal static readonly (Color From,Color To,string Name)[] Presets=[
        (Color.FromRgb(255,154,139),Color.FromRgb(255,106,136),"산호"),
        (Color.FromRgb(132,250,176),Color.FromRgb(143,211,244),"민트"),
        (Color.FromRgb(161,140,209),Color.FromRgb(251,194,235),"라벤더"),
        (Color.FromRgb(252,203,144),Color.FromRgb(213,126,235),"노을"),
        (Color.FromRgb(67,206,162),Color.FromRgb(24,90,157),"바다"),
        (Color.FromRgb(246,211,101),Color.FromRgb(253,160,133),"햇살"),
        (Color.FromRgb(48,57,82),Color.FromRgb(21,25,38),"밤"),
        (Color.FromRgb(238,242,247),Color.FromRgb(203,213,225),"안개")];

    /// <param name="image">The picture a blurred fill is made from; without one it falls back to the gradient preset.</param>
    internal static Brush Fill(Backdrop backdrop,BitmapSource? image=null)=>backdrop.Fill switch {
        BackdropFill.Solid=>Frozen(new SolidColorBrush(Color.FromArgb(255,backdrop.Color.R,backdrop.Color.G,backdrop.Color.B))),
        BackdropFill.Transparent=>Brushes.Transparent,
        BackdropFill.Blurred when image is not null=>BlurredBrush(image),
        _=>Frozen(new LinearGradientBrush(Presets[backdrop.Preset].From,Presets[backdrop.Preset].To,new Point(0,0),new Point(1,1)))};

    private static Brush BlurredBrush(BitmapSource image)
    {
        var brush=new ImageBrush(Blurred(image)) {Stretch=Stretch.UniformToFill};
        RenderOptions.SetBitmapScalingMode(brush,BitmapScalingMode.HighQuality);return Frozen(brush);
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource,BitmapSource> blurred=new();
    /// <summary>
    /// A soft, colour-only version of the image: shrunk to a few dozen pixels, enlarged and blurred.
    /// It is small, so the editor preview and the export stretch the same bitmap and match.
    /// </summary>
    internal static BitmapSource Blurred(BitmapSource image)=>blurred.GetValue(image,static source=>{
        const int Tiny=24,Soft=192;
        double scale=(double)Tiny/Math.Max(source.PixelWidth,source.PixelHeight);
        int w=Math.Max(1,(int)Math.Round(source.PixelWidth*scale)),h=Math.Max(1,(int)Math.Round(source.PixelHeight*scale));
        var tiny=Render(w,h,dc=>dc.DrawImage(source,new Rect(0,0,w,h)));
        int sw=Math.Max(1,w*Soft/Tiny),sh=Math.Max(1,h*Soft/Tiny);
        // Drawn past every edge, so the blur never pulls in transparency at the border.
        return Render(sw,sh,dc=>dc.DrawImage(tiny,new Rect(-sw*.1,-sh*.1,sw*1.2,sh*1.2)),new BlurEffect {Radius=Soft/12.0,KernelType=KernelType.Gaussian,RenderingBias=RenderingBias.Quality});
    });
    private static BitmapSource Render(int width,int height,Action<DrawingContext> draw,Effect? effect=null)
    {
        var visual=new DrawingVisual {Effect=effect};RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
        using(var dc=visual.RenderOpen())draw(dc);
        var output=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);output.Render(visual);output.Freeze();return output;
    }
    /// <summary>The part of the original image under the crop, which a blurred fill is made from.</summary>
    internal static BitmapSource BlurSource(BitmapSource source,RectD crop)
    {
        var area=new Int32Rect((int)Math.Max(0,Math.Floor(crop.X)),(int)Math.Max(0,Math.Floor(crop.Y)),0,0);
        area.Width=Math.Min(source.PixelWidth,(int)Math.Ceiling(crop.Right))-area.X;area.Height=Math.Min(source.PixelHeight,(int)Math.Ceiling(crop.Bottom))-area.Y;
        if(area.Width<1 || area.Height<1 || (area.X==0 && area.Y==0 && area.Width==source.PixelWidth && area.Height==source.PixelHeight))return source;
        // Only the latest crop is kept, so dragging the crop does not pile up copies.
        var last=lastCrops.GetValue(source,_=>new());
        lock(last){if(last.Area!=area || last.Image is null){var cropped=new CroppedBitmap(source,area);cropped.Freeze();(last.Area,last.Image)=(area,cropped);}return last.Image;}
    }
    private sealed class LastCrop {internal Int32Rect Area;internal BitmapSource? Image;}
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource,LastCrop> lastCrops=new();

    /// <summary>Corner radius clamped so it never exceeds half the shorter side.</summary>
    internal static double Radius(Backdrop backdrop,double width,double height)=>Math.Min(backdrop.CornerRadius,Math.Min(width,height)/2);
    internal static double ShadowBlur(Backdrop backdrop)=>backdrop.Shadow*Math.Clamp(backdrop.PaddingPixels*.6,8,60);
    internal static double ShadowOffset(Backdrop backdrop)=>backdrop.Shadow*Math.Clamp(backdrop.PaddingPixels*.12,2,14);
    internal static double ShadowOpacity(Backdrop backdrop)=>backdrop.Shadow*.55;

    /// <param name="blurFrom">The picture a blurred fill is made from; the content itself when omitted.</param>
    internal static BitmapSource Compose(BitmapSource content,Backdrop backdrop,BitmapSource? blurFrom=null)
    {
        if(!backdrop.Enabled)return content;
        int pad=backdrop.PaddingPixels,width=content.PixelWidth,height=content.PixelHeight;
        long total=(long)(width+2*pad)*(height+2*pad);
        if(total>64_000_000)throw new InvalidOperationException("배경을 포함한 이미지가 6400만 픽셀을 넘습니다. 여백을 줄여 주세요.");
        var inner=new Rect(pad,pad,width,height);var radius=Radius(backdrop,width,height);
        var root=new ContainerVisual();
        var background=new DrawingVisual();
        using(var dc=background.RenderOpen())dc.DrawRectangle(Fill(backdrop,blurFrom??content),null,new Rect(0,0,width+2*pad,height+2*pad));
        root.Children.Add(background);
        if(backdrop.Shadow>0) {
            var shadow=new DrawingVisual {Effect=new BlurEffect {Radius=ShadowBlur(backdrop),KernelType=KernelType.Gaussian,RenderingBias=RenderingBias.Quality},Opacity=ShadowOpacity(backdrop)};
            using(var dc=shadow.RenderOpen())dc.DrawRoundedRectangle(Brushes.Black,null,new Rect(inner.X,inner.Y+ShadowOffset(backdrop),inner.Width,inner.Height),radius,radius);
            root.Children.Add(shadow);
        }
        var image=new DrawingVisual();
        using(var dc=image.RenderOpen()) {
            dc.PushClip(new RectangleGeometry(inner,radius,radius));
            dc.DrawImage(content,inner);dc.Pop();
        }
        root.Children.Add(image);
        var output=new RenderTargetBitmap(width+2*pad,height+2*pad,96,96,PixelFormats.Pbgra32);
        output.Render(root);output.Freeze();return output;
    }

    /// <summary>Rendered annotations plus backdrop — the image every copy/save/pin path exports.</summary>
    internal static BitmapSource Export(BitmapSource source,EditableDocument document)=>Scale(Compose(ImageRenderer.Render(source,document),document.Backdrop,BlurSource(source,document.Crop)),document.ExportScale);
    /// <summary>High-quality resize for export; 1 returns the image unchanged.</summary>
    internal static BitmapSource Scale(BitmapSource image,double scale)
    {
        if(scale==1)return image;
        int width=Math.Max(1,(int)Math.Round(image.PixelWidth*scale)),height=Math.Max(1,(int)Math.Round(image.PixelHeight*scale));
        var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
        using(var dc=visual.RenderOpen())dc.DrawImage(image,new Rect(0,0,width,height));
        var output=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);output.Render(visual);output.Freeze();return output;
    }

    private static T Frozen<T>(T freezable) where T:Freezable {freezable.Freeze();return freezable;}
}
