using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>
/// Renders fixed annotation scenes on the same 960×640 test grid used for the reference app observations
/// (research/test-grid.png, reproduced here pixel-exactly), so Snaplet output can be compared side by side
/// with the original app. Each scene uses the editor's default (first) preset unless noted, which is what a new user gets.
/// Coordinates are documented in docs/fidelity.md so the same scene can be drawn in the reference app.
/// </summary>
internal static class FidelitySheet
{
    internal static BitmapSource TestGrid()
    {
        const int w=960,h=640;var px=new byte[w*h*4];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++) {
            (byte r,byte g,byte b) c=(245,246,250);
            if(x is >=60 and <440 && y is >=60 and <280)c=(32,105,210);
            else if(x is >=520 and <900 && y is >=60 and <280)c=(240,175,40);
            else if(x is >=60 and <900 && y is >=360 and <580)c=((((x-40)/40)+((y-360)/40))%2==1)?((byte)220,(byte)60,(byte)70):((byte)250,(byte)250,(byte)250);
            int i=(y*w+x)*4;px[i]=c.b;px[i+1]=c.g;px[i+2]=c.r;px[i+3]=255;
        }
        var bmp=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,px,w*4);bmp.Freeze();return bmp;
    }

    private static Annotation A(AnnotationKind kind,double x1,double y1,double x2,double y2,EditorPreset preset,string text="",PointD? control=null,PointD? tail=null,AnnotationStyle? style=null,RectD? sample=null)=>
        new(Guid.NewGuid(),kind,new(x1,y1),new(x2,y2),preset.Color,preset.Size,text,Control:control,Tail:tail,Style:style??preset.Style,SampleRegion:sample);

    internal static void Run(string directory)
    {
        var grid=TestGrid();
        void Scene(string name,Action<EditableDocument> build)
        {
            var doc=new EditableDocument(grid.PixelWidth,grid.PixelHeight);build(doc);
            var image=BackdropRenderer.Export(grid,doc);
            var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(image));
            using var f=File.Create(Path.Combine(directory,"fidelity-"+name+".png"));e.Save(f);
        }
        var arrows=EditorPresets.For(AnnotationKind.Arrow);
        var shapes=EditorPresets.For(AnnotationKind.Rectangle);
        var texts=EditorPresets.For(AnnotationKind.Text);
        var steps=EditorPresets.For(AnnotationKind.Step);
        var marks=EditorPresets.For(AnnotationKind.Highlight);
        var pens=EditorPresets.For(AnnotationKind.Pen);
        var red=new EditorPreset("reference-red",new ColorRgba(255,13,0),4,new AnnotationStyle());

        // S1: the exact arrow from research/reference-test-edited.png (reference app default red filled arrow), default Snaplet preset and reference-like settings.
        Scene("s1-arrow-default",d=>d.Add(A(AnnotationKind.Arrow,155,148,680,245,arrows[0])));
        Scene("s1-arrow-filled-red",d=>d.Add(A(AnnotationKind.Arrow,155,148,680,245,red,style:new AnnotationStyle(ArrowHead:ArrowHead.Filled))));
        // S2: every arrow preset, one per row across the boxes and checker.
        Scene("s2-arrow-presets",d=>{for(int i=0;i<arrows.Count;i++)d.Add(A(AnnotationKind.Arrow,100,90+i*130,460+i*40,140+i*130,arrows[i]));});
        // S3: every head style at the same colour/size, plus a bent arrow.
        Scene("s3-arrow-heads",d=>{
            var heads=new[]{ArrowHead.Filled,ArrowHead.Open,ArrowHead.Simple,ArrowHead.Double};
            for(int i=0;i<heads.Length;i++)d.Add(A(AnnotationKind.Arrow,100,100+i*120,420,100+i*120,red,style:new AnnotationStyle(ArrowHead:heads[i])));
            d.Add(A(AnnotationKind.Arrow,560,120,860,500,red,control:new PointD(860,160),style:new AnnotationStyle(ArrowHead:ArrowHead.Filled)));
        });
        // S4: rectangle/ellipse presets and fills.
        Scene("s4-shapes",d=>{
            for(int i=0;i<shapes.Count;i++)d.Add(A(AnnotationKind.Rectangle,90+i*200,90,250+i*200,220,shapes[i]));
            var fills=new[]{ShapeFill.Outline,ShapeFill.Translucent,ShapeFill.Solid,ShapeFill.Hatched};
            for(int i=0;i<fills.Length;i++)d.Add(A(AnnotationKind.Ellipse,90+i*200,390,250+i*200,550,red,style:new AnnotationStyle(Fill:fills[i])));
        });
        Scene("s4-shapes-sketch",d=>{
            var fills=new[]{ShapeFill.Outline,ShapeFill.Translucent,ShapeFill.Solid,ShapeFill.Hatched};
            for(int i=0;i<fills.Length;i++) {
                d.Add(A(AnnotationKind.Rectangle,90+i*200,90,250+i*200,220,red,style:new AnnotationStyle(Fill:fills[i],Sketch:true)));
                d.Add(A(AnnotationKind.Ellipse,90+i*200,390,250+i*200,550,red,style:new AnnotationStyle(Fill:fills[i],Sketch:true)));
            }
        });
        // S5: text callouts (default preset first) and plain text.
        Scene("s5-text",d=>{
            for(int i=0;i<texts.Count;i++)d.Add(A(AnnotationKind.Text,120+(i%2)*420,100+(i/2)*250,320+(i%2)*420,140+(i/2)*250,texts[i],"Snaplet test",tail:new PointD(190+(i%2)*420,190+(i/2)*250)));
        });
        // S6: counters with pointers (UX03–UX05 in the investigation) and highlighter/pen.
        Scene("s6-counters-markers",d=>{
            for(int i=0;i<steps.Count;i++)d.Add(A(AnnotationKind.Step,140+i*120,170,140+i*120,170,steps[i],(i+1).ToString(),tail:new PointD(140+i*120+20,130)));
            d.Add(A(AnnotationKind.Highlight,560,120,860,200,marks[0]));
            d.Add(A(AnnotationKind.Highlight,560,230,860,250,marks[1]));
            d.Add(new Annotation(Guid.NewGuid(),AnnotationKind.Pen,new(100,420),new(800,520),pens[0].Color,pens[0].Size,Points:[new(100,420),new(220,500),new(360,430),new(520,520),new(700,440),new(800,520)],Style:pens[0].Style));
        });
        // S7: raster effects on the checker and a magnifier/spotlight.
        Scene("s7-effects",d=>{
            d.Add(A(AnnotationKind.Blur,80,380,300,560,red));
            d.Add(A(AnnotationKind.Pixelate,340,380,560,560,red));
            d.Add(A(AnnotationKind.Erase,600,380,880,560,red));
            d.Add(new Annotation(Guid.NewGuid(),AnnotationKind.Magnifier,new(560,90),new(800,250),red.Color,red.Size,SampleRegion:new RectD(400,100,80,53),Style:new AnnotationStyle(Magnification:3,Elliptical:true)));
        });
        Scene("s7-spotlight",d=>d.Add(A(AnnotationKind.Spotlight,520,60,900,280,red,style:new AnnotationStyle(Dim:.6))));
        // S8: backdrop presets.
        Scene("s8-backdrop",d=>d.SetBackdrop(new Backdrop(Enabled:true,Preset:2,Padding:64,CornerRadius:12,Shadow:.5)));
        Scene("s8-backdrop-transparent",d=>d.SetBackdrop(new Backdrop(Enabled:true,Fill:BackdropFill.Transparent,Padding:48,CornerRadius:10,Shadow:.6)));
        var original=new PngBitmapEncoder();original.Frames.Add(BitmapFrame.Create(grid));
        using(var f=File.Create(Path.Combine(directory,"fidelity-s0-grid.png")))original.Save(f);
    }
}
