using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;
using Capture.Windows.Editor;

namespace Capture.Windows.UI;

/// <summary>Saves a project to a temporary folder, reopens it and compares the rendered pixels with the original document.</summary>
internal static class ProjectSmokeTest
{
    internal static void Run(string directory)
    {
        var checks=new List<string>();void Check(bool value,string label){if(!value)throw new InvalidOperationException("Project check failed: "+label);checks.Add(label);}
        var folder=Path.Combine(Path.GetTempPath(),"snaplet-project-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try {
            var image=ImageActions.Demo();var document=new EditableDocument(image.PixelWidth,image.PixelHeight);
            document.Add(new Annotation(Guid.NewGuid(),AnnotationKind.Rectangle,new(80,80),new(500,300),new(240,65,70),6));
            document.Add(new Annotation(Guid.NewGuid(),AnnotationKind.Text,new(120,120),new(420,180),new(255,255,255),24,"프로젝트"));
            document.Add(new Annotation(Guid.NewGuid(),AnnotationKind.Image,new(600,400),new(610,410),new(0,0,0),Image:new RasterImage(10,10,Enumerable.Repeat((byte)200,400).ToArray())));
            document.SetBackdrop(new(true,BackdropFill.Gradient,2,default,30,8,.4));
            var path=Path.Combine(folder,"작업 파일.snaplet");
            ProjectFileIO.Save(image,document,path);
            Check(File.Exists(path) && !File.Exists(path+".tmp"),"saving leaves the project and no temporary file");
            var (loadedImage,loaded)=ProjectFileIO.Load(path);
            Check(Bytes(BackdropRenderer.Export(image,document)).SequenceEqual(Bytes(BackdropRenderer.Export(loadedImage,loaded))),"a reopened project exports exactly the same pixels");
            ProjectFileIO.Save(image,document,path);Check(ProjectFileIO.Load(path).Document.Annotations.Count==3,"saving over an existing project replaces it");
            var editor=new EditorWindow(loadedImage,loaded);
            try {editor.Show();editor.UpdateLayout();Check(editor.IsVisible && loaded.Annotations.Count==3,"the editor opens a project with its objects");}
            finally {editor.CloseForExit();}
            var broken=Path.Combine(folder,"broken.snaplet");File.WriteAllBytes(broken,[1,2,3]);
            bool rejected=false;try{ProjectFileIO.Load(broken);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"a damaged project reports an error the app can show");
            Check(ProjectFileIO.IsProject(@"C:\a\B.SNAPLET") && !ProjectFileIO.IsProject(@"C:\a\b.png"),"projects are told apart from images by extension");
        } finally {try{Directory.Delete(folder,true);}catch(IOException){}}
        File.WriteAllText(Path.Combine(directory,"project-smoke.json"),System.Text.Json.JsonSerializer.Serialize(checks));
    }
    private static byte[] Bytes(BitmapSource image)
    {
        var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);var bytes=new byte[image.PixelWidth*image.PixelHeight*4];converted.CopyPixels(bytes,image.PixelWidth*4,0);return bytes;
    }
}
