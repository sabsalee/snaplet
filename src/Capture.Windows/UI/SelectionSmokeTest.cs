using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;
using Capture.Windows.Platform;

namespace Capture.Windows.UI;

/// <summary>
/// Selection overlay on a synthetic monitor image plus settings migration and QR decoding.
/// Drives the overlay's own key handler and pointer state; it is not physical mouse/keyboard or multi-monitor DPI testing.
/// </summary>
internal static class SelectionSmokeTest
{
    internal static void Run(string directory)
    {
        var checks=new List<string>();void Check(bool value,string label){if(!value)throw new InvalidOperationException("Selection check failed: "+label);checks.Add(label);}

        // --- settings migration -------------------------------------------------------------
        const string v1="""{"AutoUpdate":true,"CopyAfterCapture":false,"SaveAfterCapture":false,"ShowPreview":true,"ClosePreviewOutside":true,"SaveFolder":"C:\\Shots","Hotkeys":[{"Command":"region","Key":"Q","Modifiers":"Control, Alt"},{"Command":"window","Key":"D2","Modifiers":"Control, Shift"},{"Command":"display","Key":"D3","Modifiers":"Control, Shift"},{"Command":"delayed","Key":"D4","Modifiers":"Control, Shift"},{"Command":"last","Key":"D5","Modifiers":"Control, Shift"},{"Command":"repeat","Key":"D6","Modifiers":"Control, Shift"}]}""";
        var migrated=SettingsStore.Parse(v1,out var warning);
        Check(migrated.SchemaVersion==AppSettings.CurrentSchema && warning is null,"0.8 settings migrate silently to the current schema");
        Check(migrated.Hotkeys.Count==AppSettings.Commands.Count && migrated.Hotkeys.Select(h=>h.Command).SequenceEqual(AppSettings.Commands.Select(c=>c.Command)),"migration adds every new command in display order");
        Check(migrated.Hotkeys[0] is {Key:Key.Q,Modifiers:ModifierKeys.Control|ModifierKeys.Alt},"customised shortcut survives migration");
        Check(migrated.Hotkeys.Where(h=>h.Command is "desktop" or "pick-window" or "scroll" or "ocr" or "home").All(h=>!h.Assigned),"new commands start unassigned instead of taking keys from other apps");
        Check(!migrated.CopyAfterCapture && migrated.SaveFolder=="C:\\Shots","other stored preferences survive migration");
        var conflicted=SettingsStore.Parse("""{"SaveFolder":"relative","Hotkeys":[{"Command":"region","Key":"D1","Modifiers":"Control, Shift"},{"Command":"ocr","Key":"D1","Modifiers":"Control, Shift"},{"Command":"bogus","Key":"F1","Modifiers":"Control"},{"Command":"region","Key":"F2","Modifiers":"Control"}]}""",out var conflictWarning);
        Check(conflicted.Hotkeys.Single(h=>h.Command=="region").Key==Key.D1 && !conflicted.Hotkeys.Single(h=>h.Command=="ocr").Assigned,"duplicate shortcut keeps the first command and unassigns the second");
        Check(conflicted.Hotkeys.All(h=>h.Command!="bogus") && conflictWarning is not null,"unknown commands are dropped with a visible warning");
        Check(conflicted.Hotkeys.Single(h=>h.Command=="window").Key==Key.D2,"missing default commands are restored with their default keys");
        Check(Path.IsPathFullyQualified(conflicted.SaveFolder),"relative save folder falls back to the default absolute folder");
        Check(SettingsStore.Parse("{}",out _).Hotkeys.Count(h=>h.Assigned)==6,"empty file yields the six default shortcuts");
        Check(new HotkeyBinding("ocr",Key.None,ModifierKeys.None).ToString()=="미지정","unassigned binding is labelled");

        // --- QR -----------------------------------------------------------------------------
        QrReader.RunSelfTest();Check(true,"QR encode → WPF bitmap → decode round trip, blank image yields nothing");

        // --- overlay ------------------------------------------------------------------------
        var image=ImageActions.Demo();
        var display=new DisplayInfo(0,new Native.RECT{Left=0,Top=0,Right=image.PixelWidth,Bottom=image.PixelHeight},new Native.RECT{Left=0,Top=0,Right=image.PixelWidth,Bottom=image.PixelHeight},"synthetic");
        SelectionResult? result=null;bool completed=false;
        var overlay=new SelectionWindow(display,image,null,new SelectionOptions(true,true,"검사"));
        overlay.Completed+=r=>{result=r;completed=true;};
        try {
            overlay.Show();overlay.UpdateLayout();
            // Loupe follows the pointer and reports exact coordinates and colour of the source pixel.
            overlay.SimulatePointer(130,360);overlay.UpdateLayout();
            var expected=PixelProbe.Hex(Pixel(image,130,360));
            Check(overlay.LoupeText.Contains("X 130") && overlay.LoupeText.Contains("Y 360") && overlay.LoupeText.Contains(expected),"loupe shows pointer pixel coordinates and colour "+expected);
            Shot(overlay,directory,"selection-loupe");
            overlay.SimulatePointer(250,450,showRuler:true);overlay.UpdateLayout();
            Check(overlay.RulerText.StartsWith("↔ "),"ruler reports the colour run under the pointer");
            Shot(overlay,directory,"selection-ruler");
            overlay.SimulatePointer(250,450,showRuler:false);

            // Adjust state: keys move, resize and grow in exact monitor pixels.
            overlay.SimulateSelect(new PixelRect(100,320,340,280),adjust:true);
            Check(overlay.State is ({X:100,Y:320,Width:340,Height:280},true),"adjust state keeps the drawn rectangle");
            overlay.SimulateKey(Key.Right);Check(overlay.State.Selection.X==101,"arrow key moves the selection by one pixel");
            overlay.SimulateKey(Key.Left);overlay.SimulateKey(Key.OemCloseBrackets);Check(overlay.State.Selection==new PixelRect(99,319,342,282),"] grows every side by one pixel");
            overlay.SimulateKey(Key.OemOpenBrackets);Check(overlay.State.Selection==new PixelRect(100,320,340,280),"[ shrinks back");
            Shot(overlay,directory,"selection-adjust");
            // Smart fit trims the uniform panel margin down to the first card (anti-aliased corners allow one pixel).
            overlay.SimulateKey(Key.A);var fit=overlay.State.Selection;
            Check(Math.Abs(fit.X-120)<=1 && Math.Abs(fit.Y-340)<=1 && Math.Abs(fit.Width-290)<=2 && Math.Abs(fit.Height-240)<=2,$"A fits the selection to content (got {fit})");
            Shot(overlay,directory,"selection-fit");
            overlay.SimulateKey(Key.Enter);
            Check(completed && result?.Region is Int32Rect r && r.X==fit.X && r.Y==fit.Y && r.Width==fit.Width && r.Height==fit.Height,"Enter returns the adjusted rectangle in desktop pixels");
        } finally {overlay.Close();}

        completed=false;result=null;
        var cancel=new SelectionWindow(display,image,null,new SelectionOptions());cancel.Completed+=r=>{result=r;completed=true;};
        try {cancel.Show();cancel.UpdateLayout();cancel.SimulateKey(Key.Escape);Check(completed && result is null,"Esc cancels without a result");}
        finally {cancel.Close();}

        completed=false;result=null;
        var whole=new SelectionWindow(display,image,null,new SelectionOptions());whole.Completed+=r=>{result=r;completed=true;};
        try {whole.Show();whole.UpdateLayout();whole.SimulateKey(Key.Enter);Check(completed && result?.Region is Int32Rect {Width:1200,Height:760},"Enter without a selection captures the whole monitor");}
        finally {whole.Close();}

        // --- pointer compositing and window framing -----------------------------------------
        var white=BitmapSource.Create(20,20,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,20*20*4).ToArray(),80);
        var redPx=new byte[4*4*4];for(int i=0;i<redPx.Length;i+=4){redPx[i+2]=255;redPx[i+3]=255;}
        var pointer=new CursorSnapshot(BitmapSource.Create(4,4,96,96,PixelFormats.Bgra32,null,redPx,16),10,10);
        var framed=pointer.DrawOnto(new CapturedFrame(white,5,5,"test"));
        var at=new byte[4];framed.Image.CopyPixels(new Int32Rect(5,5,1,1),at,4,0);
        Check(at[2]==255 && at[1]==0 && framed.X==5,"pointer is drawn at its desktop position relative to the frame");
        var away=new CapturedFrame(white,100,100,"test");Check(ReferenceEquals(pointer.DrawOnto(away),away),"pointer outside the frame leaves it untouched");
        foreach(var style in new[]{WindowCaptureStyle.Shadow,WindowCaptureStyle.Backdrop}) {
            var b=App.WindowBackdrop(style);Check(b.IsValid && b.Enabled,"window style "+style+" maps to a valid backdrop");
            Check(Capture.Windows.Editor.BackdropRenderer.Compose(white,b).PixelWidth==20+2*b.PaddingPixels,"window style "+style+" frames the window");
        }

        // --- editor crop precision reuses the same arithmetic --------------------------------
        var surface=new Capture.Windows.Editor.EditorSurface(image,new EditableDocument(image.PixelWidth,image.PixelHeight)) {CropTool=true,PendingCrop=new RectD(100,320,340,280)};
        Check(surface.FitCropToContent() && surface.PendingCrop is RectD c && Math.Abs(c.X-120)<=1 && Math.Abs(c.Width-290)<=2,"editor A fits the pending crop to content");
        var fitted=surface.PendingCrop!.Value;
        surface.NudgeCrop(1,0,false);Check(surface.PendingCrop!.Value.X==fitted.X+1,"editor arrow moves the pending crop by one pixel");
        surface.NudgeCrop(10,0,true);Check(surface.PendingCrop!.Value.Width==fitted.Width+10,"editor Alt+arrow resizes the pending crop");
        surface.GrowCrop(1);Check(surface.PendingCrop!.Value.X==fitted.X,"editor ] grows the pending crop");

        File.WriteAllText(Path.Combine(directory,"selection-smoke.json"),JsonSerializer.Serialize(new{component="selection overlay, settings migration, QR",passed=checks.Count,checks,
            limitations=new[]{"Overlay keys and pointer are driven through the overlay's handlers, not physical input","Single synthetic monitor; mixed-DPI multi-monitor selection is not covered"}},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static ColorRgba Pixel(BitmapSource image,int x,int y)
    {
        var px=new byte[4];new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0).CopyPixels(new Int32Rect(x,y,1,1),px,4,0);return PixelProbe.At(px,1,0,0);
    }
    private static void Shot(Window window,string directory,string name)
    {
        var content=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,"ui-"+name+".png"));encoder.Save(file);
    }
}
