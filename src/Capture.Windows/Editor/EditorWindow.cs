using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Capture.Core;
using Microsoft.Win32;
using Capture.Windows.UI;
using Capture.Windows.Platform;

namespace Capture.Windows.Editor;

/// <summary>Source pixels and editable objects survive hiding and reopening this window.</summary>
public sealed class EditorWindow : Window
{
    private readonly BitmapSource source;
    private readonly EditableDocument document;
    private readonly EditorSurface surface;
    private readonly ScrollViewer scroll;
    private readonly Grid stage=new();
    private readonly Canvas textLayer=new();
    private readonly Border backdropFrame=new();
    private readonly Border backdropShadow=new() {Background=Brushes.Black};
    private readonly WrapPanel propertyControls=new();
    private readonly Border inspector;
    private readonly TextBlock status=new(),dimensions=new();
    private readonly List<(AnnotationKind? Kind,Button Button)> toolButtons=[];
    private readonly Button undoButton,redoButton;
    private readonly Dictionary<AnnotationKind,(ColorRgba Color,double Size,AnnotationStyle Style)> toolStyles=[];
    private readonly CheckBox hideAfterExport=new() {Content="복사·저장 후 숨기기"};
    private bool fitMode=true,syncingControls,closingForExit;
    private int revision;
    private int exporting;
    internal bool IsExporting=>exporting>0;
    private TextBox? textEditor;
    private Annotation? editingText;
    private bool newText,composingText;
    private Guid? selectionBeforeTextEdit;
    private readonly HashSet<Key> nudgeKeys=[];
    internal TextBox? ActiveTextEditor=>textEditor;
    private string? savedPath;
    public long EstimatedImageBytes => (long)source.PixelWidth*source.PixelHeight*4 + document.RetainedImageBytes*2;
    public event Action<BitmapSource>? OcrRequested;
    public event Func<Task>? AddCaptureRequested;
    public event Action<BitmapSource>? RasterizedRequested;
    public event Action<BitmapSource>? PinRequested;
    /// <summary>Optional copy/save when Esc closes an idle editor (set from app settings).</summary>
    public EscapeAction EscapeAction {get;set;}
    public string? EscapeSaveFolder {get;set;}
    private static readonly Brush PanelBrush=Ui.Card;
    private static readonly Brush MutedBrush=Ui.Muted;

    public EditorWindow(BitmapSource image,EditableDocument? existingDocument=null,bool closeOnDismiss=false)
    {
        ArgumentNullException.ThrowIfNull(image);
        source=image.IsFrozen?image:BitmapFrame.Create(image);if(source.CanFreeze && !source.IsFrozen)source.Freeze();
        document=existingDocument ?? new(source.PixelWidth,source.PixelHeight);
        if(document.Width!=source.PixelWidth || document.Height!=source.PixelHeight)throw new ArgumentException("문서와 이미지 크기가 다릅니다.");
        surface=new(source,document) {HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        WindowAppearance.SetDarkChrome(this,true);
        Title="Snaplet — 이미지 편집";Width=1180;Height=800;MinWidth=700;MinHeight=450;
        Background=Ui.Page;Foreground=Ui.Text;FontFamily=new FontFamily("Segoe UI");
        var root=new DockPanel();Content=root;
        var toolbar=new DockPanel {Background=PanelBrush,LastChildFill=true,Height=58};DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,6,0)};DockPanel.SetDock(actions,Dock.Left);toolbar.Children.Add(actions);
        ToolButton(actions,"⧉","복사 · Ctrl+C",async()=>await CopyAsync());
        ToolButton(actions,"↧","저장 · Ctrl+S / 다른 이름으로 저장 · Ctrl+Shift+S",()=>Save(false));
        ToolButton(actions,"⌖","편집 결과 고정",()=>{if(!FinishTextEdit(true))return;PinRequested?.Invoke(BackdropRenderer.Export(source,document));});
        var backdropButton=ToolButton(actions,"▣","배경 꾸미기 · 그라데이션·여백·그림자·둥근 모서리",()=>{});backdropButton.Click+=(_,_)=>ShowBackdrop(backdropButton);
        undoButton=ToolButton(actions,"↶","실행 취소 · Ctrl+Z",()=>{if(!FinishTextEdit(true))return;surface.CancelGesture();document.Undo();});
        redoButton=ToolButton(actions,"↷","다시 실행 · Ctrl+Y",()=>{if(!FinishTextEdit(true))return;surface.CancelGesture();document.Redo();});
        var info=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(info,Dock.Right);toolbar.Children.Add(info);
        dimensions.Foreground=MutedBrush;dimensions.Margin=new Thickness(12,0,12,0);dimensions.VerticalAlignment=VerticalAlignment.Center;info.Children.Add(dimensions);
        var zoomButton=ToolButton(info,"100%","확대율 · 클릭하여 보기 옵션",()=>{});
        zoomButton.Click+=(_,_)=>{
            var menu=new ContextMenu();Item(menu,"화면 맞춤 · Ctrl+0",()=>{fitMode=true;Fit();});Item(menu,"100% · Ctrl+1",SetActualSize);Item(menu,"선택 영역 확대 · Ctrl+2",ZoomToSelection);
            Item(menu,"확대 · Ctrl++",()=>ChangeZoom(1.25));Item(menu,"축소 · Ctrl+-",()=>ChangeZoom(.8));
            zoomButton.ContextMenu=menu;menu.PlacementTarget=zoomButton;menu.IsOpen=true;
        };
        zoomButton.MinWidth=64;
        var toolStrip=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        toolbar.Children.Add(new ScrollViewer {Content=toolStrip,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        void Tool(string glyph,string tip,AnnotationKind? kind) {var b=ToolButton(toolStrip,glyph,tip,()=>SetTool(kind));toolButtons.Add((kind,b));}
        Tool("↖","선택 · V",null);Tool("➜","화살표 · A",AnnotationKind.Arrow);Tool("T","텍스트 / 말풍선 · T",AnnotationKind.Text);
        Tool("①","번호 / 포인터 · N",AnnotationKind.Step);Tool("▢","사각형 · R",AnnotationKind.Rectangle);Tool("○","타원 · O",AnnotationKind.Ellipse);
        Tool("highlight","형광펜 · H",AnnotationKind.Highlight);Tool("✎","펜 · P",AnnotationKind.Pen);Tool("▧","모자이크 · M",AnnotationKind.Pixelate);
        var more=ToolButton(toolStrip,"···","도구 및 이미지 메뉴",()=>{});
        toolStrip.Children.Remove(more);info.Children.Insert(0,more);
        toolbar.SizeChanged+=(_,_)=>{
            var slots=Math.Max(2,(int)((toolbar.ActualWidth-actions.ActualWidth-info.ActualWidth-18)/40));
            for(int i=0;i<toolButtons.Count;i++)toolButtons[i].Button.Visibility=i<slots?Visibility.Visible:Visibility.Collapsed;
        };
        more.Click+=(_,_)=>{
            var menu=new ContextMenu();
            foreach(var entry in toolButtons.Where(x=>x.Button.Visibility==Visibility.Collapsed)) {
                var kind=entry.Kind;Item(menu,entry.Button.ToolTip?.ToString() ?? ToolName(kind),()=>SetTool(kind));
            }
            Item(menu,"돋보기 · G",()=>SetTool(AnnotationKind.Magnifier));Item(menu,"Spotlight · S",()=>SetTool(AnnotationKind.Spotlight));
            Item(menu,"캡처 추가 · Ctrl+Shift+A",()=>_=CaptureIntoDocumentAsync());Item(menu,"이미지 붙여넣기 · Ctrl+V",PasteImage);Item(menu,"이미지 파일 추가 · Ctrl+O",InsertImageFile);
            Item(menu,"선 · L",()=>SetTool(AnnotationKind.Line));Item(menu,"형광펜 · H",()=>SetTool(AnnotationKind.Highlight));
            Item(menu,"흐림 · B",()=>SetTool(AnnotationKind.Blur));Item(menu,"Erase · E",()=>SetTool(AnnotationKind.Erase));Item(menu,"불투명 가림",()=>SetTool(AnnotationKind.Solid));
            menu.Items.Add(new Separator());Item(menu,"자르기 / 선택 · C",StartCrop);Item(menu,"전체 캔버스 보기",()=>{if(!FinishTextEdit(true))return;surface.ClearCrop();document.ResetCrop();});
            var snapping=new MenuItem {Header="객체 가장자리·중심에 맞추기 · Ctrl 일시 해제",IsCheckable=true,IsChecked=surface.SnapToObjects};
            snapping.Click+=(_,_)=>surface.SnapToObjects=snapping.IsChecked;menu.Items.Add(snapping);
            Item(menu,"선택 객체 복제 · Ctrl+D",surface.DuplicateSelected);Item(menu,"선택 객체 삭제 · Delete",surface.DeleteSelected);
            menu.Items.Add(new Separator());Item(menu,"다른 이름으로 저장 · Ctrl+Shift+S",()=>Save(true));
            Item(menu,"텍스트 추출",()=>{if(!FinishTextEdit(true))return;OcrRequested?.Invoke(ImageRenderer.Render(source,document));});
            Item(menu,"합쳐서 새 편집창",()=>{if(!FinishTextEdit(true))return;RasterizedRequested?.Invoke(ImageRenderer.Render(source,document));});
            more.ContextMenu=menu;menu.PlacementTarget=more;menu.IsOpen=true;
        };
        var footer=new DockPanel {Background=PanelBrush,Height=30};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        hideAfterExport.Content="복사·저장 후 숨기기";hideAfterExport.IsChecked=true;hideAfterExport.FontSize=11;hideAfterExport.Margin=new Thickness(8,3,10,0);DockPanel.SetDock(hideAfterExport,Dock.Right);footer.Children.Add(hideAfterExport);
        status.Margin=new Thickness(12,5,4,0);status.FontSize=11;status.Foreground=MutedBrush;footer.Children.Add(status);
        var workspace=new Grid {Background=CanvasBrush()};root.Children.Add(workspace);
        stage.Children.Add(surface);stage.Children.Add(textLayer);
        stage.HorizontalAlignment=HorizontalAlignment.Left;stage.VerticalAlignment=VerticalAlignment.Top;
        backdropShadow.HorizontalAlignment=HorizontalAlignment.Left;backdropShadow.VerticalAlignment=VerticalAlignment.Top;backdropShadow.IsHitTestVisible=false;
        var frameGrid=new Grid();frameGrid.Children.Add(backdropShadow);frameGrid.Children.Add(stage);backdropFrame.Child=frameGrid;
        scroll=new ScrollViewer {Content=backdropFrame,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(30),HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};workspace.Children.Add(scroll);
        inspector=new Border {Background=PanelBrush,BorderThickness=new Thickness(0,0,0,1),BorderBrush=Ui.Line,Padding=new Thickness(12,4,12,6),MinHeight=54,Child=propertyControls};
        DockPanel.SetDock(inspector,Dock.Top);root.Children.Insert(root.Children.IndexOf(workspace),inspector);
        Point? panStart=null;double panX=0,panY=0;
        scroll.PreviewMouseDown+=(_,e)=>{
            if(textEditor!=null)return;
            if(e.ChangedButton!=MouseButton.Right && !(e.ChangedButton==MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))return;
            surface.CancelGesture();panStart=e.GetPosition(scroll);panX=scroll.HorizontalOffset;panY=scroll.VerticalOffset;scroll.CaptureMouse();e.Handled=true;
        };
        scroll.PreviewMouseMove+=(_,e)=>{if(panStart is Point p){var at=e.GetPosition(scroll);scroll.ScrollToHorizontalOffset(panX+p.X-at.X);scroll.ScrollToVerticalOffset(panY+p.Y-at.Y);e.Handled=true;}};
        scroll.PreviewMouseUp+=(_,e)=>{if(panStart!=null){panStart=null;scroll.ReleaseMouseCapture();e.Handled=true;}};
        scroll.LostMouseCapture+=(_,_)=>panStart=null;
        var edgePan=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
        edgePan.Tick+=(_,_)=>{
            if(!surface.IsDragging || !surface.IsMouseCaptured || Mouse.LeftButton!=MouseButtonState.Pressed){edgePan.Stop();return;}
            PanDragAt(Mouse.GetPosition(scroll));
        };
        surface.MouseMove+=(_,_)=>{if(surface.IsDragging && surface.IsMouseCaptured)edgePan.Start();};
        Deactivated+=(_,_)=>edgePan.Stop();Closed+=(_,_)=>edgePan.Stop();
        // Viewport dimensions are current here; SizeChanged can still expose the previous layout.
        scroll.ScrollChanged+=(_,e)=>{if(fitMode && (e.ViewportWidthChange!=0 || e.ViewportHeightChange!=0))Fit();};
        PreviewMouseWheel+=(_,e)=>{if(textEditor==null && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){ChangeZoom(e.Delta>0?1.25:.8,e.GetPosition(scroll));e.Handled=true;}};
        document.Changed+=(_,_)=>{revision++;undoButton.IsEnabled=document.CanUndo;redoButton.IsEnabled=document.CanRedo;if(fitMode)Fit();SyncStage();UpdateStatus();SyncInspector();};
        surface.OperationFailed+=message=>NoticeWindow.Alert(this,"편집 작업",message);
        surface.PointerHintChanged+=hint=>{if(textEditor==null && !string.IsNullOrEmpty(hint))status.Text=hint;};
        surface.SelectionChanged+=SyncInspector;surface.TextEditRequested+=BeginTextEdit;
        PreviewKeyDown+=OnKey;
        PreviewKeyUp+=(_,e)=>NudgeKeyUp(e.Key);
        surface.LostKeyboardFocus+=(_,_)=>FinishNudge();
        Deactivated+=(_,_)=>FinishNudge();
        PreviewMouseDown+=(_,e)=>{FinishNudge();if(textEditor!=null && !textEditor.IsMouseOver && !FinishTextEdit(true))e.Handled=true;};
        Closing+=(_,e)=>{if(closeOnDismiss){closingForExit=true;return;}if(!closingForExit){e.Cancel=true;if(!FinishTextEdit(true))return;Hide();}};
        Closed+=(_,_)=>closingForExit=true;
        Loaded+=(_,_)=>{Fit();surface.Selected=document.Annotations.FirstOrDefault()?.Id;surface.Focus();SyncInspector();};
        undoButton.IsEnabled=document.CanUndo;redoButton.IsEnabled=document.CanRedo;
        void UpdateZoom()=>zoomButton.Content=$"{surface.Zoom*VisualTreeHelper.GetDpi(surface).DpiScaleX:P0}";
        surface.LayoutUpdated+=(_,_)=>UpdateZoom();
    }
    private static Brush CanvasBrush()
    {
        var drawing=new DrawingGroup();
        using(var dc=drawing.Open()) {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(49,52,55)),null,new Rect(0,0,20,20));
            var alternate=new SolidColorBrush(Color.FromRgb(55,58,61));
            dc.DrawRectangle(alternate,null,new Rect(0,0,10,10));dc.DrawRectangle(alternate,null,new Rect(10,10,10,10));
        }
        var brush=new DrawingBrush(drawing){TileMode=TileMode.Tile,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,20,20)};brush.Freeze();return brush;
    }
    private static Button ToolButton(Panel parent,string glyph,string tip,Action action)
    {
        var button=new Button {Content=Ui.Icon(glyph),ToolTip=tip,MinWidth=36,Height=36,Margin=new Thickness(2),Padding=new Thickness(7,2,7,2),FontSize=21,Background=Brushes.Transparent,Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        button.Style=(Style)Application.Current.FindResource("EditorIconButton");
        Ui.BindIcon((FrameworkElement)button.Content,button);
        System.Windows.Automation.AutomationProperties.SetName(button,tip.Split('·')[0].Trim());
        ToolTipService.SetInitialShowDelay(button,450);ToolTipService.SetShowDuration(button,10000);
        button.Click+=(_,_)=>action();parent.Children.Add(button);return button;
    }
    private static void Item(ContextMenu menu,string title,Action action){var item=new MenuItem {Header=title};item.Click+=(_,_)=>action();menu.Items.Add(item);}
    private void SyncStage(){stage.Width=surface.Width;stage.Height=surface.Height;SyncBackdrop();}
    /// <summary>Live backdrop preview around the canvas, scaled with zoom. Export uses <see cref="BackdropRenderer"/> with the same values.</summary>
    private void SyncBackdrop()
    {
        var b=document.Backdrop;var z=surface.Zoom;
        if(!b.Enabled){backdropFrame.Padding=new Thickness(0);backdropFrame.Background=null;backdropShadow.Visibility=Visibility.Collapsed;surface.Clip=null;return;}
        backdropFrame.Padding=new Thickness(b.PaddingPixels*z);
        backdropFrame.Background=b.Fill==BackdropFill.Transparent?CheckerBrush():BackdropRenderer.Fill(b);
        var radius=BackdropRenderer.Radius(b,document.Crop.Width,document.Crop.Height)*z;
        surface.Clip=radius>0?new RectangleGeometry(new Rect(0,0,surface.Width,surface.Height),radius,radius):null;
        backdropShadow.Visibility=b.Shadow>0?Visibility.Visible:Visibility.Collapsed;
        backdropShadow.Width=surface.Width;backdropShadow.Height=surface.Height;backdropShadow.CornerRadius=new CornerRadius(radius);
        backdropShadow.Margin=new Thickness(0,BackdropRenderer.ShadowOffset(b)*z,0,0);backdropShadow.Opacity=BackdropRenderer.ShadowOpacity(b);
        backdropShadow.Effect=new System.Windows.Media.Effects.BlurEffect{Radius=BackdropRenderer.ShadowBlur(b)*z};
    }
    private static Brush? checker;
    private static Brush CheckerBrush()
    {
        if(checker!=null)return checker;
        var group=new DrawingGroup();using(var dc=group.Open()){dc.DrawRectangle(Brushes.White,null,new Rect(0,0,16,16));dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(220,224,230)),null,new Rect(0,0,8,8));dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(220,224,230)),null,new Rect(8,8,8,8));}
        checker=new DrawingBrush(group){TileMode=TileMode.Tile,Viewport=new Rect(0,0,16,16),ViewportUnits=BrushMappingMode.Absolute};checker.Freeze();return checker;
    }
    /// <summary>Backdrop panel: on/off, gradient swatches, transparent/solid, padding, corner radius and shadow sliders. Each change is undoable.</summary>
    private void ShowBackdrop(Button anchor)
    {
        if(!FinishTextEdit(true))return;document.EndEdit();
        var popup=new Popup {PlacementTarget=anchor,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
        var panel=new StackPanel {Width=320};
        var header=new DockPanel {Margin=new Thickness(4,2,4,10)};
        var enabled=new CheckBox {Content="사용",IsChecked=document.Backdrop.Enabled,VerticalAlignment=VerticalAlignment.Center};System.Windows.Automation.AutomationProperties.SetName(enabled,"배경 사용");
        DockPanel.SetDock(enabled,Dock.Right);header.Children.Add(enabled);
        header.Children.Add(new TextBlock {Text="배경 꾸미기",FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});panel.Children.Add(header);
        void Apply(Func<Backdrop,Backdrop> change){try{document.SetBackdrop(change(document.Backdrop));enabled.IsChecked=document.Backdrop.Enabled;}catch(Exception error) when(error is ArgumentException or InvalidOperationException){status.Text=error.Message;}}
        enabled.Click+=(_,_)=>Apply(b=>b with {Enabled=enabled.IsChecked==true});
        var swatches=new WrapPanel {Margin=new Thickness(2,0,2,8)};panel.Children.Add(swatches);
        Button Swatch(Brush fill,string name,Func<Backdrop,Backdrop> change)
        {
            var button=new Button {Width=32,Height=32,Margin=new Thickness(3),Padding=new Thickness(0),ToolTip=name,Content=new Border {Background=fill,CornerRadius=new CornerRadius(6),Width=26,Height=26}};
            System.Windows.Automation.AutomationProperties.SetName(button,"배경 "+name);
            button.Click+=(_,_)=>Apply(b=>change(b) with {Enabled=true});swatches.Children.Add(button);return button;
        }
        for(int i=0;i<Backdrop.PresetCount;i++) {
            var index=i;var preset=BackdropRenderer.Presets[i];
            var brush=new LinearGradientBrush(preset.From,preset.To,new Point(0,0),new Point(1,1));brush.Freeze();
            Swatch(brush,preset.Name,b=>b with {Fill=BackdropFill.Gradient,Preset=index});
        }
        Swatch(CheckerBrush(),"투명",b=>b with {Fill=BackdropFill.Transparent});
        Swatch(new SolidColorBrush(Color.FromRgb(surface.Ink.R,surface.Ink.G,surface.Ink.B)),"현재 색 단색",b=>b with {Fill=BackdropFill.Solid,Color=surface.Ink with {A=255}});
        void SliderRow(string label,double min,double max,Func<Backdrop,double> read,Func<Backdrop,double,Backdrop> write,string format)
        {
            var row=new DockPanel {Margin=new Thickness(4,4,4,4)};
            var value=new TextBlock {Width=44,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Foreground=MutedBrush};DockPanel.SetDock(value,Dock.Right);row.Children.Add(value);
            var name=new TextBlock {Text=label,Width=60,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(name,Dock.Left);row.Children.Add(name);
            var slider=new Slider {Minimum=min,Maximum=max,Value=read(document.Backdrop),VerticalAlignment=VerticalAlignment.Center};System.Windows.Automation.AutomationProperties.SetName(slider,"배경 "+label);
            row.Children.Add(slider);value.Text=read(document.Backdrop).ToString(format);
            slider.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>document.BeginEdit()));
            slider.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>document.EndEdit()));
            slider.ValueChanged+=(_,_)=>{value.Text=slider.Value.ToString(format);Apply(b=>write(b,slider.Value) with {Enabled=true});};
            panel.Children.Add(row);
        }
        SliderRow("여백",0,200,b=>b.Padding,(b,v)=>b with {Padding=Math.Round(v)},"0");
        SliderRow("둥글기",0,60,b=>b.CornerRadius,(b,v)=>b with {CornerRadius=Math.Round(v)},"0");
        SliderRow("그림자",0,1,b=>b.Shadow,(b,v)=>b with {Shadow=Math.Round(v,2)},"0.00");
        panel.Children.Add(new TextBlock {Text="복사·저장·고정에 그대로 포함됩니다 · Ctrl+Z로 되돌리기",FontSize=11,Foreground=MutedBrush,Margin=new Thickness(4,8,4,2),TextWrapping=TextWrapping.Wrap});
        popup.Child=new Border {Child=panel,Background=PanelBrush,BorderBrush=Ui.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(8)};
        panel.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;anchor.Focus();e.Handled=true;}};
        popup.Closed+=(_,_)=>{document.EndEdit();surface.Focus();};
        popup.IsOpen=true;
    }
    internal void PanDragAt(Point cursor)
    {
        if(!surface.IsDragging)return;
        double Edge(double p,double size)=>p<28?-Math.Min(24,(28-p)*.6):p>size-28?Math.Min(24,(p-size+28)*.6):0;
        var x=scroll.HorizontalOffset;var y=scroll.VerticalOffset;
        scroll.ScrollToHorizontalOffset(x+Edge(cursor.X,scroll.ActualWidth-SystemParameters.VerticalScrollBarWidth));
        scroll.ScrollToVerticalOffset(y+Edge(cursor.Y,scroll.ActualHeight-SystemParameters.HorizontalScrollBarHeight));
        scroll.UpdateLayout();
        if(scroll.HorizontalOffset!=x || scroll.VerticalOffset!=y)
            surface.UpdatePointer(surface.PointerPosition(scroll.TranslatePoint(cursor,surface)),Keyboard.Modifiers);
    }
    private void SetTool(AnnotationKind? kind)
    {
        if(!FinishTextEdit(true))return;document.EndEdit();surface.CancelGesture();surface.ClearCrop();
        if((surface.Selection?.Kind ?? surface.Tool) is AnnotationKind previous)toolStyles[previous]=(surface.Ink,surface.Thickness,surface.InkStyle);
        if(kind is AnnotationKind next) {
            var first=EditorPresets.For(next)[0];
            var defaults=next is AnnotationKind.Arrow or AnnotationKind.Text or AnnotationKind.Step or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Highlight or AnnotationKind.Pen or AnnotationKind.Line
                ?(first.Color,first.Size,first.Style):(new ColorRgba(240,65,70,255),4d,new AnnotationStyle(Elliptical:next==AnnotationKind.Magnifier));
            var preset=toolStyles.GetValueOrDefault(next,defaults);
            surface.Ink=preset.Item1;surface.Thickness=preset.Item2;surface.InkStyle=preset.Item3;
        }
        surface.Tool=kind;surface.Selected=null;surface.UpdateHover(new(-100,-100));surface.Focus();SyncInspector();
        status.Text=kind is null?"객체 선택·이동 · Alt+드래그 복제 · Delete 삭제":"드래그하여 그리기 · 파란 원은 꼬리 · 마름모는 곡률/모서리 · Shift 각도/비율 · Esc 선택 모드";
    }
    private void StartCrop(){SetTool(null);if(textEditor!=null)return;surface.CropTool=true;SyncInspector();status.Text="범위 선택·재조정 → Enter 적용 · Esc 취소 · Shift 정사각형";}
    private void SyncInspector()
    {
        if(syncingControls)return;
        // Keep the property row in place while typing so the canvas and insertion point do not jump.
        propertyControls.Visibility=textEditor==null?Visibility.Visible:Visibility.Hidden;
        if(textEditor!=null)return;
        var a=surface.Selection;
        if(a!=null){surface.Ink=a.Color;surface.Thickness=a.Thickness;surface.InkStyle=a.Appearance;}
        foreach(var (kind,button) in toolButtons)button.Background=kind==surface.Tool?new SolidColorBrush(Color.FromRgb(68,75,82)):Brushes.Transparent;
        // Preserve both mouse drags and keyboard adjustment of the active slider.
        if(Mouse.Captured is Thumb || propertyControls.Children.OfType<Slider>().Concat(propertyControls.Children.OfType<StackPanel>().SelectMany(panel=>panel.Children.OfType<Slider>())).Any(control=>control.IsKeyboardFocusWithin))return;
        propertyControls.Children.Clear();
        if(a==null && surface.Tool==null) {
            propertyControls.Children.Add(new TextBlock {Text="도구를 선택해 그리거나, 객체를 클릭해 스타일을 조절하세요.",Foreground=MutedBrush,Margin=new Thickness(4,10,0,0),FontSize=11});
            return;
        }
        syncingControls=true;
        try {
            var kind=a?.Kind ?? surface.Tool;
            if(kind is AnnotationKind.Arrow or AnnotationKind.Text or AnnotationKind.Step or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Highlight or AnnotationKind.Pen or AnnotationKind.Line) {
                var presets=new Button {Content="프리셋 ▾",Padding=new Thickness(10,5,10,5),Margin=new Thickness(2),ToolTip="완성된 스타일을 미리 보고 한 번에 적용"};
                System.Windows.Automation.AutomationProperties.SetName(presets,"스타일 프리셋");
                presets.Click+=(_,_)=>ShowPresets(presets,kind.Value);propertyControls.Children.Add(presets);
            }
            var title=new TextBlock {Text=ToolName(kind),Margin=new Thickness(2,8,10,0),Foreground=MutedBrush,FontSize=11};propertyControls.Children.Add(title);
            if(kind is not (AnnotationKind.Image or AnnotationKind.Spotlight or AnnotationKind.Erase or AnnotationKind.Blur or AnnotationKind.Pixelate)) {
            var color=ToolButton(propertyControls,"●","색상 / 직접 HEX 입력",()=>{});color.Foreground=ImageRenderer.BrushOf(surface.Ink);
            color.Click+=(_,_)=>ShowColors(color);
            }
            if(kind is not (AnnotationKind.Image or AnnotationKind.Spotlight or AnnotationKind.Erase)) {
            var slider=new Slider {Minimum=1,Maximum=20,Value=surface.Thickness,Width=85,Margin=new Thickness(7,5,9,5),VerticalAlignment=VerticalAlignment.Center,ToolTip="크기 / 굵기 / 효과 강도"};propertyControls.Children.Add(slider);
            var sizeReadout=NumericField(slider,"크기 / 굵기");propertyControls.Children.Add(sizeReadout);
            System.Windows.Automation.AutomationProperties.SetName(slider,"크기 / 굵기 / 효과 강도");
            slider.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>document.BeginEdit()));
            slider.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>{document.EndEdit();SyncInspector();}));
            slider.ValueChanged+=(_,_)=>{if(syncingControls)return;surface.Thickness=slider.Value;sizeReadout.Text=$"{slider.Value:0.#}";surface.ApplyStyle();};
            Group(slider,sizeReadout);
            }
            if(kind==AnnotationKind.Magnifier) {
                Choice(new[]{"사각 렌즈","원형 렌즈"},surface.InkStyle.Elliptical?1:0,index=>surface.InkStyle=surface.InkStyle with {Elliptical=index==1});
                ValueSlider("확대 배율",1,8,surface.InkStyle.Magnification,value=>{
                    surface.InkStyle=surface.InkStyle with {Magnification=value};
                    if(surface.Selection is Annotation lens) {var sample=AdvancedDrawing.SampleBounds(lens);surface.ChangeSelected(x=>x with {End=new(x.Start.X+sample.Width*value,x.Start.Y+sample.Height*value),Style=surface.InkStyle});}
                });
            }
            if(kind==AnnotationKind.Spotlight) {
                Choice(new[]{"사각 Spotlight","타원 Spotlight"},surface.InkStyle.Elliptical?1:0,index=>surface.InkStyle=surface.InkStyle with {Elliptical=index==1});
                ValueSlider("주변 어둡기 · 숫자 1–9",.1,.9,surface.InkStyle.Dim,value=>surface.SetSpotlightDim(value));
            }
            if(kind==AnnotationKind.Image)ValueSlider("이미지 불투명도",.05,1,surface.InkStyle.Opacity,value=>surface.InkStyle=surface.InkStyle with {Opacity=value});
            if(kind==AnnotationKind.Highlight) {
                Choice(new[]{"마커 선","자유 그리기","사각 영역"},(int)surface.InkStyle.HighlightShape,index=>surface.InkStyle=surface.InkStyle with {HighlightShape=(MarkerShape)index});
                Choice(new[]{"평평한 마감","둥근 마감","손그림 마감"},(int)surface.InkStyle.MarkerCap,index=>surface.InkStyle=surface.InkStyle with {MarkerCap=(MarkerCap)index});
            }
            if(a!=null && kind is AnnotationKind.Erase or AnnotationKind.Blur or AnnotationKind.Pixelate) {
                var textOnly=new Button {Content="텍스트만 처리",ToolTip="OCR로 찾은 글자 영역만 처리합니다",Margin=new Thickness(5),Padding=new Thickness(8,4,8,4)};
                textOnly.Click+=async(_,_)=>{textOnly.IsEnabled=false;try{await ProcessTextOnlyAsync(a);}finally{textOnly.IsEnabled=true;}};propertyControls.Children.Add(textOnly);
            }
            if(kind==AnnotationKind.Arrow) {
                StyleButtons(AnnotationKind.Arrow,new[]{("리본 화살표",surface.InkStyle with {ArrowHead=ArrowHead.Filled}),("열린 곡선 화살표",surface.InkStyle with {ArrowHead=ArrowHead.Open}),("가는 화살표",surface.InkStyle with {ArrowHead=ArrowHead.Simple}),("양방향",surface.InkStyle with {ArrowHead=ArrowHead.Double})});
                if(a!=null)ToolButton(propertyControls,"⇄","시작점과 끝점 반전",()=>surface.ChangeSelected(x=>x with {Start=x.End,End=x.Start}));
            }
            if(kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse)Choice(new[]{"테두리","반투명 채움","단색 채움","사선 채움"},(int)surface.InkStyle.Fill,index=>surface.InkStyle=surface.InkStyle with {Fill=(ShapeFill)index});
            if(kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse) {
                var sketch=new CheckBox {Content="손그림",IsChecked=surface.InkStyle.Sketch,Margin=new Thickness(8,8,4,0),ToolTip="살짝 휘어진 선과 부드러운 모서리"};propertyControls.Children.Add(sketch);
                sketch.Click+=(_,_)=>{surface.InkStyle=surface.InkStyle with {Sketch=sketch.IsChecked==true};surface.ApplyStyle();};
            }
            if(kind==AnnotationKind.Rectangle || kind==AnnotationKind.Text && surface.InkStyle.Bubble)
                ValueSlider("모서리 둥글기",0,48,surface.InkStyle.CornerRadius,value=>surface.InkStyle=surface.InkStyle with {CornerRadius=value});
            if(kind==AnnotationKind.Text) {
                Choice(new[]{"말풍선","일반 글자"},surface.InkStyle.Bubble?0:1,index=>surface.InkStyle=surface.InkStyle with {Bubble=index==0});
                if(a!=null)ToolButton(propertyControls,"✎","글자 편집 · 더블클릭",()=>BeginTextEdit(a,false));
            }
            if(kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Step || kind==AnnotationKind.Text && surface.InkStyle.Bubble) {
                var shapeTail=kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse;
                var tail=new CheckBox {Content="말꼬리",IsChecked=shapeTail?surface.InkStyle.ShapeTail:surface.InkStyle.ShowTail,Margin=new Thickness(8,8,4,0),ToolTip="파란 원을 드래그해 방향·길이 조절 · 민트색 마름모로 모서리 조절"};propertyControls.Children.Add(tail);
                tail.Click+=(_,_)=>{surface.InkStyle=shapeTail?surface.InkStyle with {ShapeTail=tail.IsChecked==true}:surface.InkStyle with {ShowTail=tail.IsChecked==true};surface.ApplyStyle();SyncInspector();};
            }
            if(kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Text && AnnotationGeometry.HasTail(a ?? new Annotation(Guid.NewGuid(),kind.Value,new(0,0),new(80,40),surface.Ink,Style:surface.InkStyle)))
                ValueSlider("말꼬리 너비",8,64,surface.InkStyle.TailWidth,value=>surface.InkStyle=surface.InkStyle with {TailWidth=value});
            if(a?.Kind==AnnotationKind.Step) {
                var value=new TextBox {Text=a.Text,Width=48,Padding=new Thickness(6,4,6,4),Margin=new Thickness(7,5,0,5),ToolTip="번호 0–99 · Enter 적용"};propertyControls.Children.Add(value);
                value.KeyDown+=(_,e)=>{if(e.Key==Key.Enter && int.TryParse(value.Text,out int n) && n is >=0 and <=99){surface.ChangeSelected(x=>x with {Text=n.ToString()});surface.Focus();e.Handled=true;}};
            }
        } finally {syncingControls=false;}
        void ValueSlider(string label,double minimum,double maximum,double value,Action<double> update) {
            var caption=new TextBlock {Text=label.Split('·')[0].Trim(),FontSize=11,Foreground=MutedBrush,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5)};propertyControls.Children.Add(caption);
            var control=new Slider {Minimum=minimum,Maximum=maximum,Value=value,Width=85,Margin=new Thickness(6),ToolTip=label,VerticalAlignment=VerticalAlignment.Center};
            System.Windows.Automation.AutomationProperties.SetName(control,label);propertyControls.Children.Add(control);
            var readout=NumericField(control,label);propertyControls.Children.Add(readout);
            control.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>document.BeginEdit()));
            control.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>{document.EndEdit();SyncInspector();}));
            control.ValueChanged+=(_,_)=>{if(syncingControls)return;try{readout.Text=$"{control.Value:0.#}";update(control.Value);surface.ApplyStyle();}catch(Exception error) when(error is InvalidOperationException or ArgumentException){document.CancelEdit();status.Text=error.Message;SyncInspector();}};
            Group(caption,control,readout);
        }
        void Group(params UIElement[] controls) {
            var row=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            foreach(var control in controls){propertyControls.Children.Remove(control);row.Children.Add(control);}propertyControls.Children.Add(row);
        }
        void Choice(string[] labels,int selected,Action<int> update) {
            var combo=new ComboBox {ItemsSource=labels,SelectedIndex=selected,MinWidth=90,Margin=new Thickness(4),VerticalAlignment=VerticalAlignment.Center};propertyControls.Children.Add(combo);
            combo.SelectionChanged+=(_,_)=>{if(syncingControls || combo.SelectedIndex<0)return;update(combo.SelectedIndex);surface.ApplyStyle();};
        }
    }
    private TextBox NumericField(Slider slider,string label)
    {
        var input=new TextBox {Text=$"{slider.Value:0.#}",Width=44,FontSize=11,Padding=new Thickness(5,4,5,4),Margin=new Thickness(2),VerticalContentAlignment=VerticalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,ToolTip=$"{label} · {slider.Minimum:0.#}–{slider.Maximum:0.#} · 숫자 입력 후 Enter 적용 / Esc 취소"};
        System.Windows.Automation.AutomationProperties.SetName(input,label+" 숫자 입력");
        input.PreviewKeyDown+=(_,e)=>{
            if(e.Key==Key.Escape){input.Text=$"{slider.Value:0.#}";surface.Focus();e.Handled=true;}
            else if(e.Key==Key.Enter) {
                e.Handled=true;
                if(double.TryParse(input.Text,out var number) && double.IsFinite(number) && number>=slider.Minimum && number<=slider.Maximum) {input.ClearValue(Control.BorderBrushProperty);slider.Value=number;surface.Focus();}
                else {input.BorderBrush=Brushes.IndianRed;input.SelectAll();}
            }
        };
        return input;
    }
    private void ShowPresets(Button anchor,AnnotationKind kind)
    {
        var popup=new Popup {PlacementTarget=anchor,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
        var panel=new StackPanel {Width=300};
        panel.Children.Add(new TextBlock {Text="스타일 프리셋",FontWeight=FontWeights.SemiBold,Margin=new Thickness(6,4,6,8)});
        foreach(var preset in EditorPresets.For(kind)) {
            var sample=preset.Sample(kind);var drawing=new DrawingGroup();
            using(var dc=drawing.Open()) {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(247,249,252)),null,new Rect(0,0,164,70));
                ImageRenderer.PaintAnnotation(dc,source,sample);
            }
            var row=new DockPanel();var preview=new Image {Source=new DrawingImage(drawing),Width=132,Height=56,Margin=new Thickness(0,0,10,0)};DockPanel.SetDock(preview,Dock.Left);row.Children.Add(preview);
            row.Children.Add(new TextBlock {Text=preset.Name,VerticalAlignment=VerticalAlignment.Center,FontSize=12});
            var button=new Button {Content=row,HorizontalContentAlignment=HorizontalAlignment.Left,Margin=new Thickness(2),Padding=new Thickness(6),ToolTip=preset.Name+" · 선택 객체와 다음 그리기에 적용"};
            System.Windows.Automation.AutomationProperties.SetName(button,preset.Name);
            button.Click+=(_,_)=>{surface.Ink=preset.Color;surface.Thickness=preset.Size;surface.InkStyle=preset.Style;surface.ApplyStyle();toolStyles[kind]=(preset.Color,preset.Size,preset.Style);popup.IsOpen=false;SyncInspector();surface.Focus();};panel.Children.Add(button);
        }
        popup.Child=new Border {Child=panel,Background=PanelBrush,BorderBrush=Ui.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(8)};
        KeyboardNavigation.SetTabNavigation(panel,KeyboardNavigationMode.Cycle);
        popup.Opened+=(_,_)=>panel.Children.OfType<Button>().FirstOrDefault()?.Focus();
        panel.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;anchor.Focus();e.Handled=true;}};
        popup.IsOpen=true;
    }
    private void StyleButtons(AnnotationKind kind,(string Label,AnnotationStyle Style)[] choices)
    {
        foreach(var (label,style) in choices) {
            var group=new DrawingGroup();
            using(var dc=group.Open()) {
                var sample=new Annotation(Guid.Parse("a25323c7-bb59-4ef2-aacb-3cb304ef3011"),kind,new(3,14),new(35,14),new(240,242,246),1.6,Style:style);
                ImageRenderer.PaintAnnotation(dc,source,sample);
            }
            var button=new Button {Content=new Image {Source=new DrawingImage(group),Width=32,Height=22,Stretch=Stretch.Uniform},ToolTip=label,Width=40,Height=34,Padding=new Thickness(4),Margin=new Thickness(1,3,1,3),Background=style.ArrowHead==surface.InkStyle.ArrowHead?(Brush)Application.Current.FindResource("AccentFillBrush"):Brushes.Transparent};
            System.Windows.Automation.AutomationProperties.SetName(button,label);
            button.Click+=(_,_)=>{surface.InkStyle=style;surface.ApplyStyle();SyncInspector();};propertyControls.Children.Add(button);
        }
    }
    private static string ToolName(AnnotationKind? kind)=>kind switch {AnnotationKind.Magnifier=>"돋보기",AnnotationKind.Spotlight=>"Spotlight",AnnotationKind.Image=>"추가 이미지",AnnotationKind.Erase=>"Erase",AnnotationKind.Arrow=>"화살표",AnnotationKind.Text=>"텍스트",AnnotationKind.Step=>"번호",AnnotationKind.Rectangle=>"사각형",AnnotationKind.Ellipse=>"타원",AnnotationKind.Pen=>"펜",AnnotationKind.Line=>"선",AnnotationKind.Pixelate=>"모자이크",AnnotationKind.Blur=>"흐림",AnnotationKind.Highlight=>"형광펜",_=>"가림"};
    private void ShowColors(Button anchor)
    {
        var popup=new Popup {PlacementTarget=anchor,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
        var panel=new StackPanel();popup.Child=new Border {Background=PanelBrush,Padding=new Thickness(10),CornerRadius=new CornerRadius(6),Child=panel};
        var palette=new WrapPanel {Width=220};panel.Children.Add(palette);
        foreach(var c in new[]{Colors.Crimson,Colors.Red,Colors.Orange,Colors.Gold,Colors.LimeGreen,Colors.Teal,Colors.DodgerBlue,Colors.MediumPurple,Colors.Black,Colors.DimGray,Colors.White,Colors.LightGray}) {
            var button=new Button {Style=(Style)Application.Current.FindResource("SwatchButton"),Background=new SolidColorBrush(c),Width=30,Height=28,Margin=new Thickness(3),BorderThickness=new Thickness(1),ToolTip=c.ToString()};palette.Children.Add(button);
            button.Click+=(_,_)=>{surface.Ink=new(c.R,c.G,c.B,c.A);surface.ApplyStyle();popup.IsOpen=false;SyncInspector();};
        }
        var hex=new TextBox {Text=$"#{surface.Ink.R:X2}{surface.Ink.G:X2}{surface.Ink.B:X2}",Margin=new Thickness(3,8,3,3),ToolTip="#RRGGBB 또는 #AARRGGBB · Enter 적용"};panel.Children.Add(hex);
        hex.KeyDown+=(_,e)=>{if(e.Key!=Key.Enter)return;try {var c=(Color)ColorConverter.ConvertFromString(hex.Text);surface.Ink=new(c.R,c.G,c.B,c.A);surface.ApplyStyle();popup.IsOpen=false;SyncInspector();}catch(FormatException){hex.ToolTip="색상 형식을 확인하세요: #FF6633";}e.Handled=true;};
        popup.IsOpen=true;
    }
    internal void BeginTextEdit(Annotation a,bool create)
    {
        if(!FinishTextEdit(true))return;if(textEditor!=null)return;surface.CancelGesture();selectionBeforeTextEdit=surface.Selected;editingText=a;newText=create;composingText=false;surface.Selected=create?null:a.Id;
        surface.SetTextEditing(create?null:a.Id);
        textEditor=new TextBox {Style=(Style)Application.Current.FindResource("EditorInlineTextBox"),Text=a.Text,MaxLength=8192,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,
            Foreground=a.Appearance.Bubble?ImageRenderer.Contrast(a.Color):ImageRenderer.BrushOf(a.Color),Background=a.Appearance.Bubble?ImageRenderer.BrushOf(a.Color):Brushes.Transparent,Opacity=a.Appearance.Opacity,BorderBrush=Brushes.DodgerBlue,BorderThickness=new Thickness(2)};
        textLayer.Children.Add(textEditor);UpdateTextEditorLayout();
        textEditor.TextChanged+=(_,_)=>UpdateTextEditorLayout();
        TextCompositionManager.AddPreviewTextInputStartHandler(textEditor,(_,_)=>composingText=true);
        TextCompositionManager.AddPreviewTextInputHandler(textEditor,(_,_)=>composingText=false);
        textEditor.PreviewKeyDown+=(_,e)=>{if(composingText)return;if(e.Key==Key.Escape){FinishTextEdit(false);e.Handled=true;}else if(e.Key==Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){FinishTextEdit(true);e.Handled=true;}};
        textEditor.Focus();textEditor.CaretIndex=textEditor.Text.Length;SyncInspector();
        status.Text="이미지 위에 입력하세요 · Enter 줄바꿈 · Ctrl+Enter 완료 · Esc 취소";
    }
    private void UpdateTextEditorLayout()
    {
        if(textEditor is null || editingText is not Annotation a)return;
        var z=surface.Zoom;var preview=EditedText(a,textEditor.Text);surface.SetTextPreview(preview);
        var b=ImageRenderer.TextBounds(preview);
        EditorChrome.SetCornerRadius(textEditor,new CornerRadius(a.Appearance.Bubble?Math.Min(a.Appearance.CornerRadius,Math.Min(b.Width,b.Height)/2)*z+2:2));
        textEditor.FontSize=Math.Max(14,a.Thickness*5)*z;
        textEditor.Padding=a.Appearance.Bubble?new Thickness(10*z,8*z,10*z,8*z):new Thickness(0);
        textEditor.Width=b.Width*z+4;textEditor.Height=Math.Min(32768,b.Height)*z+4;
        textEditor.VerticalScrollBarVisibility=b.Height>32768?ScrollBarVisibility.Auto:ScrollBarVisibility.Hidden;
        Canvas.SetLeft(textEditor,(b.X-document.Crop.X)*z-2);Canvas.SetTop(textEditor,(b.Y-document.Crop.Y)*z-2);
        // The editable layer may grow while typing; the image and document are unchanged until commit.
        textLayer.Width=Math.Max(surface.Width,(b.Right-document.Crop.X)*z+4);
        textLayer.Height=Math.Max(surface.Height,(b.Y-document.Crop.Y)*z+textEditor.Height+4);
        stage.Width=Math.Max(surface.Width,textLayer.Width);stage.Height=Math.Max(surface.Height,textLayer.Height);
    }
    private Annotation EditedText(Annotation original,string value)
    {
        var updated=original with {Text=value};
        if(newText && updated.Appearance.Bubble && updated.Appearance.ShowTail) {
            var b=ImageRenderer.TextBounds(updated);var crop=document.Crop;
            var y=b.Bottom+28<=crop.Bottom?b.Bottom+28:b.Top-28>=crop.Y?b.Top-28:b.Bottom+28;
            updated=updated with {Tail=new(b.Left+b.Width*.35,y)};
        }
        return updated;
    }
    internal bool FinishTextEdit(bool commit)
    {
        FinishNudge();
        if(textEditor==null || editingText is not Annotation a)return true;
        if(commit && composingText){status.Text="한글 조합을 끝낸 뒤 다시 완료하세요.";textEditor.Focus();return false;}
        var value=textEditor.Text;
        try {
            if(commit) {
                if(!string.IsNullOrWhiteSpace(value)) {
                    // Keep the width used during typing, including an existing manually resized body.
                    var updated=EditedText(a,value);
                    if(newText) {
                        var bounds=ImageRenderer.TextBounds(updated);var crop=document.Crop;
                        var tipY=bounds.Bottom+18<=crop.Bottom?bounds.Bottom+18:Math.Max(crop.Y,bounds.Top-18);
                        updated=updated with {Tail=new(bounds.X+bounds.Width*.35,tipY)};
                    }
                    surface.CommitText(updated,newText);surface.Selected=a.Id;
                } else if(!newText)document.Delete(a.Id);
            }
        } catch(Exception error) when(error is ArgumentException or InvalidOperationException or OverflowException) {status.Text=error.Message;UpdateTextEditorLayout();textEditor.Focus();return false;}
        textLayer.Children.Clear();textLayer.ClearValue(WidthProperty);textLayer.ClearValue(HeightProperty);textEditor=null;editingText=null;composingText=false;surface.SetTextEditing(null);
        if(!commit)surface.Selected=selectionBeforeTextEdit;
        SyncStage();if(fitMode)Fit();surface.Focus();SyncInspector();return true;
    }
    private void Fit()
    {
        if(textEditor!=null || scroll.ViewportWidth<=1 || scroll.ViewportHeight<=1)return;
        var pad=2*document.Backdrop.PaddingPixels;
        surface.SetZoom(Math.Min(1,Math.Min(Math.Max(1,scroll.ViewportWidth-60)/(document.Crop.Width+pad),Math.Max(1,scroll.ViewportHeight-60)/(document.Crop.Height+pad))));SyncStage();UpdateStatus();
    }
    private bool FitCrop(){if(!surface.FitCropToContent())status.Text="가장자리에 단색 여백이 없습니다.";return true;}
    private void UpdateCropStatus(){if(surface.PendingCrop is RectD c)status.Text=$"자르기 {c.Width:0}×{c.Height:0} · 방향키 이동 · Alt+방향키 크기 · [ ] 확장/축소 · A 내용에 맞춤 · Enter 적용";}
    /// <summary>Ctrl+2: zoom so the pending crop or the selected object fills the view.</summary>
    private void ZoomToSelection()
    {
        RectD? target=surface.PendingCrop ?? surface.Selection?.Bounds;
        if(target is not RectD r || r.Width<1 || r.Height<1 || scroll.ViewportWidth<=1){status.Text="확대할 선택 영역이 없습니다. 자르기 범위나 객체를 먼저 선택하세요.";return;}
        fitMode=false;
        surface.SetZoom(Math.Min((scroll.ViewportWidth-60)/r.Width,(scroll.ViewportHeight-60)/r.Height));SyncStage();scroll.UpdateLayout();
        var center=surface.TranslatePoint(new Point((r.X+r.Width/2-document.Crop.X)*surface.Zoom,(r.Y+r.Height/2-document.Crop.Y)*surface.Zoom),scroll);
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset+center.X-scroll.ViewportWidth/2);
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset+center.Y-scroll.ViewportHeight/2);
        scroll.UpdateLayout();UpdateStatus();
    }
    private void SetActualSize()=>ZoomAt(1/VisualTreeHelper.GetDpi(surface).DpiScaleX,new(scroll.ActualWidth/2,scroll.ActualHeight/2));
    private void ChangeZoom(double factor,Point? anchor=null)=>ZoomAt(surface.Zoom*factor,anchor ?? new Point(scroll.ActualWidth/2,scroll.ActualHeight/2));
    internal void ZoomAt(double zoom,Point anchor)
    {
        if(!FinishTextEdit(true))return;surface.CancelGesture();scroll.UpdateLayout();
        var before=scroll.TranslatePoint(anchor,surface);var oldZoom=surface.Zoom;
        fitMode=false;surface.SetZoom(zoom);SyncStage();scroll.UpdateLayout();
        var after=surface.TranslatePoint(new Point(before.X/oldZoom*surface.Zoom,before.Y/oldZoom*surface.Zoom),scroll);
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset+after.X-anchor.X);
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset+after.Y-anchor.Y);
        scroll.UpdateLayout();UpdateStatus();
    }
    internal void NudgeKeyDown(Key key,bool shift)
    {
        if(surface.Selection is null)return;
        if(nudgeKeys.Count==0){surface.CancelGesture();document.BeginEdit();}
        nudgeKeys.Add(key);var step=shift?10:1;
        surface.Nudge(key==Key.Left?-step:key==Key.Right?step:0,key==Key.Up?-step:key==Key.Down?step:0);
    }
    internal void NudgeKeyUp(Key key){if(nudgeKeys.Count==1 && nudgeKeys.Contains(key))FinishNudge();else nudgeKeys.Remove(key);}
    private void FinishNudge(){if(nudgeKeys.Count==0)return;nudgeKeys.Clear();document.EndEdit();}
    private void UpdateStatus(){var pad=2*document.Backdrop.PaddingPixels;dimensions.Text=pad>0?$"{document.Crop.Width+pad:0}×{document.Crop.Height+pad:0} (배경 포함)":$"{document.Crop.Width:0}×{document.Crop.Height:0}";status.Text=$"{document.Annotations.Count}개 주석 · Ctrl+휠 확대 · Space / 오른쪽 드래그 이동 · C 자르기 · V 선택";}
    private void OnKey(object sender,KeyEventArgs e)
    {
        if(e.OriginalSource is TextBox || textEditor!=null || IsPropertyInput(e.OriginalSource as DependencyObject))return;
        if(e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))FinishNudge();
        var ctrl=Keyboard.Modifiers.HasFlag(ModifierKeys.Control);var shift=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if(surface.CropTool && surface.PendingCrop!=null && !ctrl) {
            // Crop precision like the capture overlay: arrows move, Alt+arrows resize, [ ] grow/shrink, A fits to content.
            var key=e.Key==Key.System?e.SystemKey:e.Key;var step=shift?10:1;var alt=Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            int dx=key==Key.Left?-step:key==Key.Right?step:0,dy=key==Key.Up?-step:key==Key.Down?step:0;
            bool handled=key switch {
                Key.Left or Key.Right or Key.Up or Key.Down=>surface.NudgeCrop(dx,dy,alt),
                Key.OemOpenBrackets=>surface.GrowCrop(-step),Key.OemCloseBrackets=>surface.GrowCrop(step),
                Key.A=>FitCrop(),_=>false};
            if(handled){UpdateCropStatus();e.Handled=true;return;}
        }
        if(ctrl) {
            switch(e.Key) {
                case Key.Z:surface.CancelGesture();if(shift)document.Redo();else document.Undo();break;
                case Key.Y:surface.CancelGesture();document.Redo();break;
                case Key.V:PasteImage();break;case Key.O:InsertImageFile();break;
                case Key.A:if(shift)_=CaptureIntoDocumentAsync();else return;break;
                case Key.Enter:if(surface.Selection is {Kind:AnnotationKind.Highlight})surface.ChangeSelected(a=>a with {Style=a.Appearance with {MarkerCap=(MarkerCap)(((int)a.Appearance.MarkerCap+1)%3)}});else return;break;
                case Key.C:_=CopyAsync();break;case Key.S:Save(shift);break;
                case Key.D:surface.DuplicateSelected();break;
                case Key.D0:fitMode=true;Fit();break;case Key.D1:SetActualSize();break;case Key.D2:ZoomToSelection();break;
                case Key.OemPlus:case Key.Add:ChangeZoom(1.25);break;case Key.OemMinus:case Key.Subtract:ChangeZoom(.8);break;
                default:return;
            }
            e.Handled=true;return;
        }
        switch(e.Key) {
            case Key.Delete:surface.DeleteSelected();break;
            case Key.Escape:if(surface.Tool is null && !surface.CropTool && surface.Selected is null)_=DismissWithEscapeAsync();else SetTool(null);break;
            case Key.Enter:surface.ApplyCrop();break;
            case Key.V:SetTool(null);break;case Key.A:SetTool(AnnotationKind.Arrow);break;case Key.L:SetTool(AnnotationKind.Line);break;
            case Key.R:SetTool(AnnotationKind.Rectangle);break;case Key.O:SetTool(AnnotationKind.Ellipse);break;case Key.P:SetTool(AnnotationKind.Pen);break;
            case Key.T:SetTool(AnnotationKind.Text);break;case Key.N:SetTool(AnnotationKind.Step);break;case Key.H:SetTool(AnnotationKind.Highlight);break;
            case Key.G:SetTool(AnnotationKind.Magnifier);break;case Key.S:SetTool(AnnotationKind.Spotlight);break;case Key.E:SetTool(AnnotationKind.Erase);break;
            case Key.M:SetTool(AnnotationKind.Pixelate);break;case Key.B:SetTool(AnnotationKind.Blur);break;case Key.C:StartCrop();break;
            case Key.Left:case Key.Right:case Key.Up:case Key.Down:NudgeKeyDown(e.Key,shift);break;
            default:return;
        }
        e.Handled=true;
    }
    private static bool IsPropertyInput(DependencyObject? target)
    {
        while(target!=null) {
            if(target is ComboBox or ComboBoxItem or Slider)return true;
            target=target is Visual?VisualTreeHelper.GetParent(target):LogicalTreeHelper.GetParent(target);
        }
        return false;
    }
    private async Task ProcessTextOnlyAsync(Annotation selected)
    {
        try {
            if(!FinishTextEdit(true))return;document.EndEdit();
            var capturedRevision=revision;
            var region=selected.Bounds.Intersect(document.Canvas);
            if(region.IsEmpty)return;
            var snapshot=new EditableDocument(source.PixelWidth,source.PixelHeight);snapshot.ExpandToInclude(document.Canvas);
            foreach(var a in document.Annotations.Where(a=>a.Id!=selected.Id && a.Kind!=AnnotationKind.Spotlight))snapshot.Add(a);
            snapshot.CropTo(region);var image=ImageRenderer.Render(source,snapshot);
            status.Text="처리할 글자 영역을 찾는 중…";
            var boxes=await TesseractOcr.FindTextRegionsAsync(image);
            if(revision!=capturedRevision || surface.Selected!=selected.Id || document.Annotations.FirstOrDefault(a=>a.Id==selected.Id)!=selected){status.Text="선택 영역이 바뀌어 텍스트 처리를 취소했습니다.";return;}
            ApplyTextRegions(selected,region,boxes);
        } catch(Exception error){ShowError("텍스트 영역 처리",error);}
    }
    internal void ApplyTextRegions(Annotation selected,RectD region,IReadOnlyList<Rect> boxes)
    {
        if(document.Annotations.FirstOrDefault(a=>a.Id==selected.Id)!=selected)return;
        var rectangles=boxes.Select(box=>new RectD(Math.Floor(region.X)+box.X-2,Math.Floor(region.Y)+box.Y-2,box.Width+4,box.Height+4).Intersect(region)).Where(r=>!r.IsEmpty).ToArray();
        if(rectangles.Length>200)throw new InvalidOperationException("문자 영역이 너무 많습니다. 더 작은 영역에서 처리해 주세요.");
        if(rectangles.Length==0){status.Text="인식된 글자가 없어 기존 효과를 유지했습니다.";return;}
        document.BeginEdit();
        try {
            document.Delete(selected.Id);Guid? first=null;
            foreach(var r in rectangles) {var id=Guid.NewGuid();first??=id;document.Add(selected with {Id=id,Start=new(r.X,r.Y),End=new(r.Right,r.Bottom)});}
            document.EndEdit();surface.Selected=first;surface.Focus();SyncInspector();status.Text=$"{rectangles.Length}개 글자 영역만 처리했습니다 · Ctrl+Z 취소";
        } catch {document.CancelEdit();throw;}
    }
    public void AddCapturedImage(BitmapSource image)
    {
        if(!FinishTextEdit(true))return;surface.CancelGesture();surface.InsertImage(image);status.Text="이미지를 추가했습니다 · 핸들로 크기 조절 · Ctrl+Z 취소";
    }
    private void PasteImage()
    {
        try {if(Clipboard.GetImage() is BitmapSource image)AddCapturedImage(image);else if(Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(path=>new[]{".png",".jpg",".jpeg",".bmp",".tif",".tiff",".gif"}.Contains(Path.GetExtension(path).ToLowerInvariant())) is string path)AddCapturedImage(ImageActions.Load(path));else status.Text="클립보드에 이미지가 없습니다.";}
        catch(Exception error){ShowError("이미지 붙여넣기",error);}
    }
    private void InsertImageFile()
    {
        var dialog=new OpenFileDialog {Filter="이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif"};
        if(dialog.ShowDialog(this)!=true)return;
        try {AddCapturedImage(ImageActions.Load(dialog.FileName));}catch(Exception error){ShowError("이미지 추가",error);}
    }
    private async Task CaptureIntoDocumentAsync()
    {
        if(!FinishTextEdit(true))return;surface.CancelGesture();
        if(AddCaptureRequested is null){status.Text="캡처 추가는 실행 중인 Snaplet에서 사용할 수 있습니다.";return;}
        try {await AddCaptureRequested.Invoke();}catch(Exception error){ShowError("캡처 추가",error);}
    }
    private async Task CopyAsync()
    {
        if(!FinishTextEdit(true))return;document.EndEdit();exporting++;
        try {
            var copied=revision;var bitmap=BackdropRenderer.Export(source,document);
            if(!await ImageActions.CopyAsync(bitmap)){status.Text="더 최근 복사 요청이 있어 취소했습니다.";return;}
            status.Text="편집 이미지를 복사했습니다.";if(revision==copied && hideAfterExport.IsChecked==true)Hide();
        }catch(Exception ex){ShowError("이미지 복사 실패",ex);}
        finally {exporting--;}
    }
    private void Save(bool saveAs)
    {
        if(!FinishTextEdit(true))return;document.EndEdit();var path=savedPath;
        if(saveAs || path==null) {
            var dialog=new SaveFileDialog {Filter="PNG 이미지 (*.png)|*.png|JPEG 이미지 (*.jpg)|*.jpg",FileName=path ?? $"Snaplet_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png",AddExtension=true,OverwritePrompt=true};
            if(dialog.ShowDialog(this)!=true)return;path=dialog.FileName;
        }
        try {ImageActions.Save(BackdropRenderer.Export(source,document),path);savedPath=path;status.Text=$"저장 완료: {path}";if(hideAfterExport.IsChecked==true)Hide();}
        catch(Exception ex){ShowError("이미지 저장 실패",ex);}
    }
    /// <summary>Esc on an idle editor closes it like the original, optionally copying or saving first. The document stays reopenable.</summary>
    private async Task DismissWithEscapeAsync()
    {
        if(!FinishTextEdit(true))return;document.EndEdit();
        try {
            if(EscapeAction==EscapeAction.Copy) {
                exporting++;try{await ImageActions.CopyAsync(BackdropRenderer.Export(source,document));}finally{exporting--;}
            } else if(EscapeAction==EscapeAction.Save && EscapeSaveFolder is string folder) {
                Directory.CreateDirectory(folder);var path=Path.Combine(folder,ImageActions.DefaultName());
                ImageActions.Save(BackdropRenderer.Export(source,document),path);savedPath=path;
            }
        } catch(Exception ex){ShowError("Esc로 닫기 전 내보내기 실패",ex);return;}
        Close();
    }
    public void ShowDocument(){Show();Activate();if(textEditor!=null)textEditor.Focus();else surface.Focus();}
    public void CloseForExit(){closingForExit=true;Close();}
    private void ShowError(string title,Exception ex)=>NoticeWindow.Alert(this,title,ex.Message);
}
