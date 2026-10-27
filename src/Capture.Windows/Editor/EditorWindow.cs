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
    private readonly StackPanel propertyRow=new() {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
    private readonly WrapPanel propertyDetails=new() {MaxWidth=470,Margin=new Thickness(2,4,2,2)};
    private readonly Border inspector;
    private readonly TextBlock status=new(),dimensions=new(),zoomText=new(),hoverHex=new() {Text="—"};
    private readonly Border statusToast;
    private readonly System.Windows.Shapes.Ellipse hoverSwatch=new() {Width=14,Height=14,StrokeThickness=1,Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center};
    private readonly List<(AnnotationKind? Kind,Button Button)> toolButtons=[];
    private readonly Button moreToolSlot;
    private readonly Action<double> layoutTools;
    // Toolbar buttons are 36 px wide with 2 px margins; the first eight tools stay inline before the colour readout.
    private const double ToolSlotWidth=40;
    private const int CoreToolCount=8;
    private readonly Dictionary<AnnotationKind,(ColorRgba Color,double Size,AnnotationStyle Style)> toolStyles=[];
    private bool hideAfterExport=true,inspectorExpanded,inspectorCollapsed,darkTheme;
    private ColorRgba? hoverColor;
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
    internal bool UsesDarkTheme=>darkTheme;
    /// <summary>Shows the secondary property controls (presets, numeric input, tails, modes) below the compact row.</summary>
    internal bool InspectorExpanded {get=>inspectorExpanded;set{inspectorExpanded=value;SyncInspector();}}
    private string? savedPath;
    public long EstimatedImageBytes => (long)source.PixelWidth*source.PixelHeight*4 + document.RetainedImageBytes*2;
    public event Action<BitmapSource>? OcrRequested;
    public event Func<Task>? AddCaptureRequested;
    public event Action<BitmapSource>? RasterizedRequested;
    public event Action<BitmapSource>? PinRequested;
    /// <summary>Optional copy/save when Esc closes an idle editor (set from app settings).</summary>
    public EscapeAction EscapeAction {get;set;}
    public string? EscapeSaveFolder {get;set;}
    // Secondary tools, shown inline while the toolbar has room. Once it runs out they fold behind ··· from the end,
    // and the last one chosen there takes the slot before ··· like the reference app.
    private static readonly AnnotationKind[] MoreTools=[AnnotationKind.Blur,AnnotationKind.Erase,AnnotationKind.Highlight,AnnotationKind.Spotlight,AnnotationKind.Step,AnnotationKind.Ellipse,AnnotationKind.Line,AnnotationKind.Magnifier,AnnotationKind.Solid];

    public EditorWindow(BitmapSource image,EditableDocument? existingDocument=null,bool closeOnDismiss=false)
    {
        ArgumentNullException.ThrowIfNull(image);
        source=image.IsFrozen?image:BitmapFrame.Create(image);if(source.CanFreeze && !source.IsFrozen)source.Freeze();
        document=existingDocument ?? new(source.PixelWidth,source.PixelHeight);
        if(document.Width!=source.PixelWidth || document.Height!=source.PixelHeight)throw new ArgumentException("문서와 이미지 크기가 다릅니다.");
        surface=new(source,document) {HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        ApplyTheme(EditorTheme.SystemUsesDark());
        Title="Snaplet — 이미지 편집";Width=1180;Height=800;MinWidth=700;MinHeight=450;
        SetResourceReference(BackgroundProperty,"PageBrush");SetResourceReference(ForegroundProperty,"TextBrush");FontFamily=new FontFamily("Segoe UI");
        var root=new DockPanel();Content=root;
        // One toolbar row like the reference: export · pin · file drag | tools ··· | colour under cursor · image size · zoom.
        var toolbar=new DockPanel {LastChildFill=true,Height=52};toolbar.SetResourceReference(Panel.BackgroundProperty,"ToolbarBrush");
        var toolbarFrame=new Border {Child=toolbar,BorderThickness=new Thickness(0,0,0,1)};toolbarFrame.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");
        DockPanel.SetDock(toolbarFrame,Dock.Top);root.Children.Add(toolbarFrame);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0)};DockPanel.SetDock(actions,Dock.Left);toolbar.Children.Add(actions);
        ToolButton(actions,"⧉","복사 · Ctrl+C",async()=>await CopyAsync());
        ToolButton(actions,"↧","저장 · Ctrl+S / 다른 이름으로 저장 · Ctrl+Shift+S",()=>Save(false));
        Divider(actions);
        ToolButton(actions,"⌖","편집 결과 고정",()=>{if(!FinishTextEdit(true))return;PinRequested?.Invoke(BackdropRenderer.Export(source,document));});
        Divider(actions);
        FileDragButton(actions);
        Divider(actions);
        var info=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,8,0)};DockPanel.SetDock(info,Dock.Right);toolbar.Children.Add(info);
        var hoverValue=new StackPanel {Orientation=Orientation.Horizontal};hoverValue.Children.Add(hoverSwatch);hoverValue.Children.Add(hoverHex);
        hoverSwatch.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"BorderBrush");
        var hoverInfo=InfoBlock(hoverValue,"Tab 복사");hoverInfo.ToolTip="커서 아래 색 · Tab으로 HEX 복사 · I 스포이트·대비 검사";hoverInfo.MinWidth=96;info.Children.Add(hoverInfo);
        System.Windows.Automation.AutomationProperties.SetName(hoverInfo,"커서 아래 색");
        var sizeInfo=InfoBlock(dimensions,"이미지 크기");sizeInfo.ToolTip="내보내기 크기 (배경·내보내기 배율 포함)";info.Children.Add(sizeInfo);
        var zoomButton=new Button {Content=InfoBlock(zoomText,"확대"),ToolTip="확대율 · 클릭하여 보기 옵션",Padding=new Thickness(6,2,6,2),Margin=new Thickness(2,0,0,0),MinHeight=40,Style=(Style)Application.Current.FindResource("EditorIconButton")};
        System.Windows.Automation.AutomationProperties.SetName(zoomButton,"확대율");info.Children.Add(zoomButton);
        zoomButton.Click+=(_,_)=>{
            var menu=Menu();Item(menu,"화면 맞춤 · Ctrl+0",()=>{fitMode=true;Fit();});Item(menu,"100% · Ctrl+1",SetActualSize);Item(menu,"선택 영역 확대 · Ctrl+2",ZoomToSelection);
            Item(menu,"확대 · Ctrl++",()=>ChangeZoom(1.25));Item(menu,"축소 · Ctrl+-",()=>ChangeZoom(.8));
            zoomButton.ContextMenu=menu;menu.PlacementTarget=zoomButton;menu.IsOpen=true;
        };
        var imageMenu=ToolButton(info,"≡","이미지 메뉴 · 실행 취소·배경·자르기·내보내기",()=>{});
        imageMenu.Click+=(_,_)=>ShowImageMenu(imageMenu);
        var toolStrip=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        toolbar.Children.Add(new ScrollViewer {Content=toolStrip,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        void Tool(string glyph,string tip,AnnotationKind? kind) {var b=ToolButton(toolStrip,glyph,tip,()=>SetTool(kind));toolButtons.Add((kind,b));}
        Tool("↖","선택 · V",null);Tool("➜","화살표 · A",AnnotationKind.Arrow);Tool("T","텍스트 / 말풍선 · T",AnnotationKind.Text);
        Tool("▢","사각형 · R",AnnotationKind.Rectangle);Tool("▧","모자이크 가림 · M",AnnotationKind.Pixelate);Tool("✎","펜 · P",AnnotationKind.Pen);
        foreach(var kind in MoreTools)Tool(ToolGlyph(kind),ToolTipOf(kind),kind);
        var inlineTools=toolButtons.Count;
        moreToolSlot=ToolButton(toolStrip,ToolGlyph(AnnotationKind.Line),ToolTipOf(AnnotationKind.Line),()=>{});
        moreToolSlot.Click+=(_,_)=>SetTool(toolButtons.Single(x=>x.Button==moreToolSlot).Kind);toolButtons.Add((AnnotationKind.Line,moreToolSlot));
        var more=ToolButton(toolStrip,"···","더 보기 · 캡처 추가·이미지 붙여넣기·스포이트·접힌 도구",()=>{});
        double hoverWidth=0;
        layoutTools=toolbarWidth=>{
            if(hoverInfo.IsVisible)hoverWidth=Math.Max(hoverWidth,hoverInfo.ActualWidth);
            if(double.IsNaN(toolbarWidth))toolbarWidth=toolbar.ActualWidth;
            var room=toolbarWidth-actions.ActualWidth-(info.ActualWidth-hoverInfo.ActualWidth)-more.ActualWidth-24;
            // The colour readout gives way before the core tools do.
            var showHover=room-hoverWidth>=CoreToolCount*ToolSlotWidth;
            hoverInfo.Visibility=showHover?Visibility.Visible:Visibility.Collapsed;if(showHover)room-=hoverWidth;
            var fit=(int)(room/ToolSlotWidth);var shown=fit>=inlineTools?inlineTools:Math.Max(2,fit-1);
            for(int i=0;i<inlineTools;i++)toolButtons[i].Button.Visibility=i<shown?Visibility.Visible:Visibility.Collapsed;
            moreToolSlot.Visibility=shown<inlineTools?Visibility.Visible:Visibility.Collapsed;
            var slotKind=toolButtons[inlineTools].Kind;
            if(shown<inlineTools && !toolButtons.Take(inlineTools).Any(x=>x.Kind==slotKind && x.Button.Visibility==Visibility.Collapsed))
                ShowInMoreSlot(surface.Tool is AnnotationKind current && IsFolded(current)?current:toolButtons[shown].Kind!.Value);
        };
        toolbar.SizeChanged+=(_,_)=>layoutTools(toolbar.ActualWidth);
        more.Click+=(_,_)=>{
            var menu=Menu();
            Item(menu,"캡처 추가 · Ctrl+Shift+A",()=>_=CaptureIntoDocumentAsync());
            Item(menu,"이미지 붙여넣기 · Ctrl+V",PasteImage);Item(menu,"이미지 파일 추가 · Ctrl+O",InsertImageFile);
            Item(menu,"스포이트·대비 검사 · I",StartColorPick);
            var folded=toolButtons.Take(inlineTools).Where(x=>x.Button.Visibility==Visibility.Collapsed).ToList();
            if(folded.Count>0)menu.Items.Add(new Separator());
            foreach(var entry in folded){var kind=entry.Kind;Item(menu,entry.Button.ToolTip?.ToString() ?? ToolName(kind),()=>SetTool(kind));}
            more.ContextMenu=menu;menu.PlacementTarget=more;menu.IsOpen=true;
        };
        var workspace=new Grid();workspace.SetResourceReference(Panel.BackgroundProperty,"WorkspaceBrush");root.Children.Add(workspace);
        stage.Children.Add(surface);stage.Children.Add(textLayer);
        stage.HorizontalAlignment=HorizontalAlignment.Left;stage.VerticalAlignment=VerticalAlignment.Top;
        backdropShadow.HorizontalAlignment=HorizontalAlignment.Left;backdropShadow.VerticalAlignment=VerticalAlignment.Top;backdropShadow.IsHitTestVisible=false;
        var frameGrid=new Grid();frameGrid.Children.Add(backdropShadow);frameGrid.Children.Add(stage);backdropFrame.Child=frameGrid;
        scroll=new ScrollViewer {Content=backdropFrame,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(30),HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};workspace.Children.Add(scroll);
        // Floating, icon-only property panel at the top right below the toolbar; it overlays the canvas instead of shifting it.
        var panel=new StackPanel();panel.Children.Add(propertyRow);panel.Children.Add(propertyDetails);
        inspector=new Border {CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),Padding=new Thickness(6,4,6,4),MinHeight=44,Child=panel,
            HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,10,14,0),
            Effect=new System.Windows.Media.Effects.DropShadowEffect {BlurRadius=14,ShadowDepth=2,Direction=270,Opacity=.18}};
        inspector.SetResourceReference(Border.BackgroundProperty,"FloatingBrush");inspector.SetResourceReference(Border.BorderBrushProperty,"FloatingBorderBrush");
        System.Windows.Automation.AutomationProperties.SetName(inspector,"속성 패널");
        workspace.Children.Add(inspector);
        // Transient messages replace the old status footer; the reference editor has none.
        status.FontSize=12;status.TextWrapping=TextWrapping.Wrap;status.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");
        statusToast=new Border {Child=status,CornerRadius=new CornerRadius(8),Padding=new Thickness(10,6,10,6),Margin=new Thickness(14,0,14,14),MaxWidth=640,BorderThickness=new Thickness(1),
            HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,IsHitTestVisible=false,Visibility=Visibility.Collapsed};
        statusToast.SetResourceReference(Border.BackgroundProperty,"FloatingBrush");statusToast.SetResourceReference(Border.BorderBrushProperty,"FloatingBorderBrush");
        workspace.Children.Add(statusToast);
        var toastTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(4)};toastTimer.Tick+=(_,_)=>{toastTimer.Stop();statusToast.Visibility=Visibility.Collapsed;};
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty,typeof(TextBlock)).AddValueChanged(status,(_,_)=>{
            toastTimer.Stop();statusToast.Visibility=string.IsNullOrEmpty(status.Text)?Visibility.Collapsed:Visibility.Visible;if(statusToast.Visibility==Visibility.Visible)toastTimer.Start();
        });
        Closed+=(_,_)=>toastTimer.Stop();
        surface.MouseMove+=(_,e)=>UpdateHoverColor(surface.PointerPosition(e.GetPosition(surface)));
        surface.MouseLeave+=(_,_)=>UpdateHoverColor(null);
        void OnPreference(object? sender,UserPreferenceChangedEventArgs e){if(e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)Dispatcher.BeginInvoke(()=>{ApplyTheme(EditorTheme.SystemUsesDark());SyncInspector();});}
        SystemEvents.UserPreferenceChanged+=OnPreference;Closed+=(_,_)=>SystemEvents.UserPreferenceChanged-=OnPreference;
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
        surface.ColorPickRequested+=PickColor;
        Deactivated+=(_,_)=>{if(colorPopup!=null)colorPopup.IsOpen=false;};Activated+=(_,_)=>{if(colorPopup!=null && surface.PickColorTool)colorPopup.IsOpen=true;};
        document.Changed+=(_,_)=>{pickCache=null;revision++;if(fitMode)Fit();SyncStage();UpdateStatus();SyncInspector();};
        surface.OperationFailed+=message=>NoticeWindow.Alert(this,"편집 작업",message);
        surface.PointerHintChanged+=hint=>{if(textEditor==null && !string.IsNullOrEmpty(hint))status.Text=hint;};
        surface.SelectionChanged+=SyncInspector;surface.SelectionChanged+=()=>{if(surface.RegionActive)UpdateCropStatus();};surface.TextEditRequested+=BeginTextEdit;
        PreviewKeyDown+=OnKey;
        PreviewKeyUp+=(_,e)=>NudgeKeyUp(e.Key);
        surface.LostKeyboardFocus+=(_,_)=>FinishNudge();
        Deactivated+=(_,_)=>FinishNudge();
        PreviewMouseDown+=(_,e)=>{FinishNudge();if(textEditor!=null && !textEditor.IsMouseOver && !FinishTextEdit(true))e.Handled=true;};
        Closing+=(_,e)=>{if(closeOnDismiss){closingForExit=true;return;}if(!closingForExit){e.Cancel=true;if(!FinishTextEdit(true))return;Hide();}};
        Closed+=(_,_)=>closingForExit=true;
        Loaded+=(_,_)=>{Fit();surface.Selected=document.Annotations.FirstOrDefault()?.Id;surface.Focus();SyncInspector();};
        void UpdateZoom()=>zoomText.Text=$"{surface.Zoom*VisualTreeHelper.GetDpi(surface).DpiScaleX:P0}";
        surface.LayoutUpdated+=(_,_)=>UpdateZoom();
    }
    private static Button ToolButton(Panel parent,string glyph,string tip,Action action)
    {
        var button=new Button {Content=Ui.Icon(glyph),ToolTip=tip,MinWidth=36,Height=36,Margin=new Thickness(2),Padding=new Thickness(7,2,7,2),FontSize=21,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        button.SetResourceReference(ForegroundProperty,"TextBrush");
        button.Style=(Style)Application.Current.FindResource("EditorIconButton");
        Ui.BindIcon((FrameworkElement)button.Content,button);
        System.Windows.Automation.AutomationProperties.SetName(button,tip.Split('·')[0].Trim());
        ToolTipService.SetInitialShowDelay(button,450);ToolTipService.SetShowDuration(button,10000);
        button.Click+=(_,_)=>action();parent.Children.Add(button);return button;
    }
    private static MenuItem Item(ContextMenu menu,string title,Action action){var item=new MenuItem {Header=title};item.Click+=(_,_)=>action();menu.Items.Add(item);return item;}
    private ContextMenu Menu()=>EditorTheme.Themed(new ContextMenu(),darkTheme);
    private Border PopupFrame(UIElement child,double padding)
    {
        var frame=new Border {Child=child,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(padding)};
        frame.SetResourceReference(Border.BackgroundProperty,"FloatingBrush");frame.SetResourceReference(Border.BorderBrushProperty,"FloatingBorderBrush");
        return EditorTheme.Themed(frame,darkTheme);
    }
    private static void Divider(Panel parent)
    {
        var line=new Border {Width=1,Height=22,Margin=new Thickness(5,0,5,0),VerticalAlignment=VerticalAlignment.Center};line.SetResourceReference(Border.BackgroundProperty,"BorderBrush");parent.Children.Add(line);
    }
    /// <summary>Value over a small caption, as on the right side of the reference toolbar.</summary>
    private static StackPanel InfoBlock(UIElement value,string caption)
    {
        var block=new StackPanel {Margin=new Thickness(8,0,8,0),VerticalAlignment=VerticalAlignment.Center};
        foreach(var text in value is Panel row?row.Children.OfType<TextBlock>():value is TextBlock single?[single]:[]) {
            text.FontSize=13;text.FontWeight=FontWeights.SemiBold;text.VerticalAlignment=VerticalAlignment.Center;text.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");
        }
        block.Children.Add(value);
        var label=new TextBlock {Text=caption,FontSize=10.5};label.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");block.Children.Add(label);
        return block;
    }
    /// <summary>Drag the edited image out as a PNG file, like the file handle in the reference toolbar.</summary>
    private void FileDragButton(Panel parent)
    {
        var button=ToolButton(parent,"🗎","파일로 드래그 · 다른 앱이나 폴더에 놓기",()=>status.Text="이 버튼을 끌어 다른 앱이나 폴더에 PNG 파일로 놓으세요.");
        Point? pressed=null;
        button.PreviewMouseLeftButtonDown+=(_,e)=>pressed=e.GetPosition(button);
        button.PreviewMouseLeftButtonUp+=(_,_)=>pressed=null;
        button.PreviewMouseMove+=(_,e)=>{
            if(pressed is not Point p || e.LeftButton!=MouseButtonState.Pressed)return;
            var at=e.GetPosition(button);
            if(Math.Abs(at.X-p.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(at.Y-p.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            pressed=null;e.Handled=true;if(!FinishTextEdit(true))return;document.EndEdit();
            try {
                var folder=Path.Combine(Path.GetTempPath(),"Snaplet");Directory.CreateDirectory(folder);
                var path=Path.Combine(folder,ImageActions.DefaultName());ImageActions.Save(BackdropRenderer.Export(source,document),path);
                DragDrop.DoDragDrop(button,new DataObject(DataFormats.FileDrop,new[]{path}),DragDropEffects.Copy);
            } catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException){ShowError("파일 드래그",error);}
        };
    }
    private void ShowImageMenu(Button anchor)
    {
        var menu=Menu();
        Item(menu,"실행 취소 · Ctrl+Z",()=>{if(!FinishTextEdit(true))return;surface.CancelGesture();document.Undo();}).IsEnabled=document.CanUndo;
        Item(menu,"다시 실행 · Ctrl+Y",()=>{if(!FinishTextEdit(true))return;surface.CancelGesture();document.Redo();}).IsEnabled=document.CanRedo;
        menu.Items.Add(new Separator());
        Item(menu,"배경 꾸미기 · 그라데이션·여백·그림자·둥근 모서리",()=>ShowBackdrop(anchor));
        Item(menu,"자르기 / 선택 · C",StartCrop);Item(menu,"전체 캔버스 보기",()=>{if(!FinishTextEdit(true))return;surface.ClearCrop();document.ResetCrop();});
        var snapping=new MenuItem {Header="객체 가장자리·중심에 맞추기 · Ctrl 일시 해제",IsCheckable=true,IsChecked=surface.SnapToObjects};
        snapping.Click+=(_,_)=>surface.SnapToObjects=snapping.IsChecked;menu.Items.Add(snapping);
        Item(menu,"선택 객체 복제 · Ctrl+D",surface.DuplicateSelected);Item(menu,"선택 객체 삭제 · Delete",surface.DeleteSelected);
        menu.Items.Add(new Separator());Item(menu,"다른 이름으로 저장 · Ctrl+Shift+S",()=>Save(true));
        Item(menu,"텍스트 추출",()=>{if(!FinishTextEdit(true))return;OcrRequested?.Invoke(ImageRenderer.Render(source,document));});
        var scaleMenu=new MenuItem {Header=$"내보내기 크기 · {document.ExportScale:P0}"};
        foreach(var (label,value) in new[]{("100% (원본)",1d),("75%",.75),("50% (200% 화면을 1배로)",.5),("25%",.25)}) {
            var item=new MenuItem {Header=label,IsCheckable=true,IsChecked=Math.Abs(document.ExportScale-value)<1e-9};var v=value;
            item.Click+=(_,_)=>{if(!FinishTextEdit(true))return;document.SetExportScale(v);};scaleMenu.Items.Add(item);
        }
        menu.Items.Add(scaleMenu);
        Item(menu,"합쳐서 새 편집창",()=>{if(!FinishTextEdit(true))return;RasterizedRequested?.Invoke(ImageRenderer.Render(source,document));});
        var hide=new MenuItem {Header="복사·저장 후 숨기기",IsCheckable=true,IsChecked=hideAfterExport};hide.Click+=(_,_)=>hideAfterExport=hide.IsChecked;menu.Items.Add(hide);
        anchor.ContextMenu=menu;menu.PlacementTarget=anchor;menu.IsOpen=true;
    }
    private void ApplyTheme(bool dark)
    {
        darkTheme=dark;EditorTheme.Themed(this,dark);
        WindowAppearance.SetDarkChrome(this,dark);WindowAppearance.Apply(this,dark);
    }
    private void UpdateHoverColor(PointD? at)
    {
        hoverColor=at is PointD p && !surface.IsDragging?SampleColor(p):null;
        hoverHex.Text=hoverColor is ColorRgba c?PixelProbe.Hex(c):"—";
        hoverSwatch.Fill=hoverColor is ColorRgba fill?new SolidColorBrush(Color.FromRgb(fill.R,fill.G,fill.B)):Brushes.Transparent;
    }
    private static string ToolGlyph(AnnotationKind kind)=>kind switch {AnnotationKind.Blur=>"blur",AnnotationKind.Erase=>"erase",AnnotationKind.Highlight=>"highlight",AnnotationKind.Spotlight=>"spotlight",
        AnnotationKind.Step=>"①",AnnotationKind.Ellipse=>"○",AnnotationKind.Line=>"line",AnnotationKind.Magnifier=>"lens",_=>"■"};
    private static string ToolTipOf(AnnotationKind kind)=>kind switch {AnnotationKind.Blur=>"흐림 · B",AnnotationKind.Erase=>"Erase · E",AnnotationKind.Highlight=>"형광펜 · H",AnnotationKind.Spotlight=>"Spotlight · S",
        AnnotationKind.Step=>"번호 / 포인터 · N",AnnotationKind.Ellipse=>"타원 · O",AnnotationKind.Line=>"선 · L",AnnotationKind.Magnifier=>"돋보기 · G",_=>"불투명 가림"};
    /// <summary>Test hook: lays the toolbar out for a given width (NaN: its real width), independent of how wide the test machine's screen lets the window grow.</summary>
    internal void LayoutToolbar(double toolbarWidth)=>layoutTools(toolbarWidth);
    private bool IsFolded(AnnotationKind kind)=>toolButtons.Any(x=>x.Kind==kind && x.Button!=moreToolSlot && x.Button.Visibility==Visibility.Collapsed);
    /// <summary>A tool picked from ··· takes the slot before ··· like the reference toolbar.</summary>
    private void ShowInMoreSlot(AnnotationKind kind)
    {
        var index=toolButtons.FindIndex(x=>x.Button==moreToolSlot);if(index<0 || toolButtons[index].Kind==kind)return;
        toolButtons[index]=(kind,moreToolSlot);
        var icon=Ui.Icon(ToolGlyph(kind));moreToolSlot.Content=icon;Ui.BindIcon(icon,moreToolSlot);moreToolSlot.ToolTip=ToolTipOf(kind);
        System.Windows.Automation.AutomationProperties.SetName(moreToolSlot,ToolTipOf(kind).Split('·')[0].Trim());
    }

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
            var value=new TextBlock {Width=44,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Foreground=Ui.Brush("MutedBrush")};DockPanel.SetDock(value,Dock.Right);row.Children.Add(value);
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
        panel.Children.Add(new TextBlock {Text="복사·저장·고정에 그대로 포함됩니다 · Ctrl+Z로 되돌리기",FontSize=11,Foreground=Ui.Brush("MutedBrush"),Margin=new Thickness(4,8,4,2),TextWrapping=TextWrapping.Wrap});
        popup.Child=PopupFrame(panel,8);
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
    // Preset sizes are in display points like the reference app; a 200% capture gets twice the source pixels.
    private double PresetScale=>Math.Max(1,VisualTreeHelper.GetDpi(this).DpiScaleX);
    internal void SetTool(AnnotationKind? kind)
    {
        if(!FinishTextEdit(true))return;document.EndEdit();surface.CancelGesture();surface.ClearCrop();StopColorPick();
        if((surface.Selection?.Kind ?? surface.Tool) is AnnotationKind previous)toolStyles[previous]=(surface.Ink,surface.Thickness,surface.InkStyle);
        if(kind is AnnotationKind next) {
            var first=EditorPresets.For(next)[0];
            var defaults=next is AnnotationKind.Arrow or AnnotationKind.Text or AnnotationKind.Step or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Highlight or AnnotationKind.Pen or AnnotationKind.Line
                ?(first.Color,first.Size*PresetScale,first.Style):(new ColorRgba(240,65,70,255),4d*PresetScale,new AnnotationStyle(Elliptical:next==AnnotationKind.Magnifier));
            var preset=toolStyles.GetValueOrDefault(next,defaults);
            surface.Ink=preset.Item1;surface.Thickness=preset.Item2;surface.InkStyle=preset.Item3;
        }
        if(kind is AnnotationKind folded && IsFolded(folded))ShowInMoreSlot(folded);
        surface.Tool=kind;surface.Selected=null;surface.UpdateHover(new(-100,-100));surface.Focus();SyncInspector();
        status.Text=kind is null?"객체 선택·이동 · 빈 곳 드래그로 범위 지정 · Alt+드래그 복제 · Delete 삭제":"드래그하여 그리기 · 파란 원은 꼬리 · 마름모는 곡률/모서리 · Shift 각도/비율 · Esc 선택 모드";
    }
    // ---- eyedropper and contrast -----------------------------------------------------------
    private BitmapSource? pickCache;
    private Popup? colorPopup;
    private ColorRgba pickForeground=new(0,0,0),pickBackground=new(255,255,255);
    private StackPanel? colorPanel;
    private void StartColorPick()
    {
        SetTool(null);if(textEditor!=null)return;
        surface.PickColorTool=true;surface.Cursor=Cursors.Cross;
        colorPanel=new StackPanel {Width=290};
        colorPopup=new Popup {PlacementTarget=dimensions,Placement=PlacementMode.Bottom,StaysOpen=true,AllowsTransparency=true,
            Child=PopupFrame(colorPanel,10)};
        RenderColorPanel();colorPopup.IsOpen=true;
        status.Text="클릭: 전경색(HEX 복사) · Shift+클릭: 배경색 · 두 색의 대비를 바로 계산합니다 · Esc 종료";
    }
    private void StopColorPick(){surface.PickColorTool=false;if(colorPopup!=null){colorPopup.IsOpen=false;colorPopup=null;}}
    /// <summary>Samples the composited (annotated) image so the colour matches what will be exported.</summary>
    internal ColorRgba? SampleColor(PointD at)
    {
        pickCache??=ImageRenderer.Render(source,document);
        int x=(int)Math.Floor(at.X)-(int)Math.Floor(document.Crop.X),y=(int)Math.Floor(at.Y)-(int)Math.Floor(document.Crop.Y);
        if(x<0 || y<0 || x>=pickCache.PixelWidth || y>=pickCache.PixelHeight)return null;
        var px=new byte[4];pickCache.CopyPixels(new Int32Rect(x,y,1,1),px,4,0);
        return new ColorRgba(px[2],px[1],px[0]);
    }
    private void PickColor(PointD at,ModifierKeys modifiers)
    {
        if(SampleColor(at) is not ColorRgba color)return;
        if(modifiers.HasFlag(ModifierKeys.Shift))pickBackground=color;else pickForeground=color;
        var hex=PixelProbe.Hex(color);
        try{Clipboard.SetText(hex);status.Text=$"{hex} 복사됨 · 대비 {ColorMath.WcagRatio(pickForeground,pickBackground):0.00}:1";}
        catch(System.Runtime.InteropServices.ExternalException){status.Text=hex+" · 다른 앱이 클립보드를 사용 중입니다";}
        RenderColorPanel();
    }
    private void RenderColorPanel()
    {
        if(colorPanel==null)return;colorPanel.Children.Clear();
        var header=new DockPanel {Margin=new Thickness(0,0,0,8)};
        var close=new Button {Content="닫기",Padding=new Thickness(8,2,8,2),FontSize=11};close.Click+=(_,_)=>SetTool(null);DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        header.Children.Add(new TextBlock {Text="스포이트 · 대비 검사",FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});colorPanel.Children.Add(header);
        void ColorRow(string label,ColorRgba color)
        {
            var row=new DockPanel {Margin=new Thickness(0,2,0,6)};
            var swatch=new Border {Width=28,Height=28,CornerRadius=new CornerRadius(6),BorderBrush=Ui.Line,BorderThickness=new Thickness(1),Background=new SolidColorBrush(Color.FromRgb(color.R,color.G,color.B)),Margin=new Thickness(0,0,10,0)};
            DockPanel.SetDock(swatch,Dock.Left);row.Children.Add(swatch);
            var formats=new WrapPanel();
            foreach(var text in new[]{PixelProbe.Hex(color),PixelProbe.Rgb(color),ColorMath.Hsl(color),ColorMath.OklchText(color)}) {
                var value=text;var b=new Button {Content=value,FontSize=11,Padding=new Thickness(6,2,6,2),Margin=new Thickness(0,0,4,4),ToolTip="클릭하여 복사"};
                System.Windows.Automation.AutomationProperties.SetName(b,label+" "+value);
                b.Click+=(_,_)=>{try{Clipboard.SetText(value);status.Text=value+" 복사됨";}catch(System.Runtime.InteropServices.ExternalException){status.Text="다른 앱이 클립보드를 사용 중입니다";}};formats.Children.Add(b);
            }
            var stack=new StackPanel();stack.Children.Add(new TextBlock {Text=label,FontSize=11,Foreground=Ui.Brush("MutedBrush"),Margin=new Thickness(0,0,0,3)});stack.Children.Add(formats);row.Children.Add(stack);
            colorPanel.Children.Add(row);
        }
        ColorRow("전경 · 클릭",pickForeground);ColorRow("배경 · Shift+클릭",pickBackground);
        var ratio=ColorMath.WcagRatio(pickForeground,pickBackground);var lc=ColorMath.ApcaLc(pickForeground,pickBackground);
        colorPanel.Children.Add(new TextBlock {Text=$"WCAG 2  {ratio:0.00}:1 · {ColorMath.WcagLevel(ratio)}",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,4,0,2)});
        colorPanel.Children.Add(new TextBlock {Text=$"APCA  Lc {lc:0.0} · 본문 권장 |Lc| ≥ 75, 큰 글자 ≥ 60",FontSize=12});
        var swap=new Button {Content="전경 ↔ 배경",FontSize=11,Padding=new Thickness(8,2,8,2),Margin=new Thickness(0,8,0,0),HorizontalAlignment=HorizontalAlignment.Left};
        swap.Click+=(_,_)=>{(pickForeground,pickBackground)=(pickBackground,pickForeground);RenderColorPanel();};colorPanel.Children.Add(swap);
    }
    /// <summary>Test hook: the same path as a click in eyedropper mode.</summary>
    internal (ColorRgba Foreground,ColorRgba Background,bool Active) PickState=>(pickForeground,pickBackground,surface.PickColorTool);
    internal void SimulatePick(PointD at,bool background){if(!surface.PickColorTool)StartColorPick();PickColor(at,background?ModifierKeys.Shift:ModifierKeys.None);}
    private void StartCrop(){SetTool(null);if(textEditor!=null)return;surface.CropTool=true;SyncInspector();status.Text="범위 선택·재조정 → Enter 적용 · Esc 취소 · Shift 정사각형";}
    private void SyncInspector()
    {
        if(syncingControls)return;
        var a=surface.Selection;
        // Hidden while typing so the inline editor is the only thing changing on the canvas.
        inspector.Visibility=textEditor==null && (a!=null || surface.Tool!=null || surface.CropTool)?Visibility.Visible:Visibility.Collapsed;
        if(textEditor!=null)return;
        if(a!=null){surface.Ink=a.Color;surface.Thickness=a.Thickness;surface.InkStyle=a.Appearance;}
        foreach(var (kind,button) in toolButtons) {if(kind==surface.Tool && !surface.CropTool)button.SetResourceReference(BackgroundProperty,"SelectedToolBrush");else button.Background=Brushes.Transparent;}
        // Preserve both mouse drags and keyboard adjustment of the active slider.
        if(Mouse.Captured is Thumb || Keyboard.FocusedElement is Slider focused && focused.IsDescendantOf(inspector))return;
        propertyRow.Children.Clear();propertyDetails.Children.Clear();
        if(a==null && surface.Tool==null){inspector.Visibility=Visibility.Collapsed;return;}
        var collapse=ToolButton(propertyRow,inspectorCollapsed?"›":"‹",inspectorCollapsed?"속성 패널 펼치기":"속성 패널 접기",()=>{inspectorCollapsed=!inspectorCollapsed;SyncInspector();});
        collapse.MinWidth=24;collapse.Height=32;collapse.FontSize=16;
        if(inspectorCollapsed){propertyDetails.Visibility=Visibility.Collapsed;return;}
        var ink=EditorTheme.Ink(this);var icon=new ColorRgba(ink.R,ink.G,ink.B);
        syncingControls=true;
        try {
            var kind=a?.Kind ?? surface.Tool;
            if(kind is AnnotationKind.Arrow or AnnotationKind.Text or AnnotationKind.Step or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Highlight or AnnotationKind.Pen or AnnotationKind.Line) {
                var presets=new Button {Content="프리셋 ▾",Padding=new Thickness(10,5,10,5),Margin=new Thickness(2),ToolTip="완성된 스타일을 미리 보고 한 번에 적용"};
                System.Windows.Automation.AutomationProperties.SetName(presets,"스타일 프리셋");
                presets.Click+=(_,_)=>ShowPresets(presets,kind.Value);propertyDetails.Children.Add(presets);
            }
            if(kind is not (AnnotationKind.Image or AnnotationKind.Spotlight or AnnotationKind.Erase or AnnotationKind.Blur or AnnotationKind.Pixelate)) {
                var dot=new System.Windows.Shapes.Ellipse {Width=18,Height=18,Fill=ImageRenderer.BrushOf(surface.Ink with {A=255}),StrokeThickness=1};dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"HandleBorderBrush");
                var color=new Button {Content=dot,ToolTip="색상 / 직접 HEX 입력",Width=32,Height=32,Padding=new Thickness(0),Margin=new Thickness(1,0,1,0),Style=(Style)Application.Current.FindResource("EditorIconButton")};
                System.Windows.Automation.AutomationProperties.SetName(color,"색상 / 직접 HEX 입력");
                color.Click+=(_,_)=>ShowColors(color);propertyRow.Children.Add(color);
            }
            if(kind is not (AnnotationKind.Image or AnnotationKind.Spotlight or AnnotationKind.Erase)) {
                var slider=new Slider {Style=(Style)Application.Current.FindResource("WedgeSlider"),Minimum=1,Maximum=20,Value=surface.Thickness,Margin=new Thickness(4,0,6,0),VerticalAlignment=VerticalAlignment.Center,ToolTip="크기 / 굵기 / 효과 강도"};
                propertyRow.Children.Add(slider);
                System.Windows.Automation.AutomationProperties.SetName(slider,"크기 / 굵기 / 효과 강도");
                var caption=new TextBlock {Text="크기",FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5)};caption.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");propertyDetails.Children.Add(caption);
                var sizeReadout=NumericField(slider,"크기 / 굵기");propertyDetails.Children.Add(sizeReadout);
                slider.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>document.BeginEdit()));
                slider.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>{document.EndEdit();SyncInspector();}));
                slider.ValueChanged+=(_,_)=>{if(syncingControls)return;surface.Thickness=slider.Value;sizeReadout.Text=$"{slider.Value:0.#}";surface.ApplyStyle();};
                Group(caption,sizeReadout);
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
                textOnly.Click+=async(_,_)=>{textOnly.IsEnabled=false;try{await ProcessTextOnlyAsync(a);}finally{textOnly.IsEnabled=true;}};propertyDetails.Children.Add(textOnly);
            }
            if(kind==AnnotationKind.Arrow) {
                foreach(var (label,head) in new[]{("리본 화살표",ArrowHead.Filled),("열린 곡선 화살표",ArrowHead.Open),("가는 화살표",ArrowHead.Simple),("양방향",ArrowHead.Double)}) {
                    var style=surface.InkStyle with {ArrowHead=head};var group=new DrawingGroup();
                    using(var dc=group.Open())ImageRenderer.PaintAnnotation(dc,source,new Annotation(Guid.Parse("a25323c7-bb59-4ef2-aacb-3cb304ef3011"),AnnotationKind.Arrow,new(3,14),new(35,14),icon,1.6,Style:style));
                    Segment(group,label,head==surface.InkStyle.ArrowHead,()=>surface.InkStyle=style);
                }
                if(a!=null){var reverse=ToolButton(propertyDetails,"⇄","시작점과 끝점 반전",()=>surface.ChangeSelected(x=>x with {Start=x.End,End=x.Start}));reverse.Height=32;}
            }
            if(kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse) {
                // The reference panel's four fills: rounded outline, outline, hatched, solid. Ellipses swap the rounded outline for translucent.
                var oval=kind==AnnotationKind.Ellipse;var style=surface.InkStyle;
                var rounded=style.CornerRadius>0?style.CornerRadius:Math.Max(1,EditorPresets.For(AnnotationKind.Rectangle)[0].Style.CornerRadius);
                if(oval)Segment(FillIcon(ShapeFill.Translucent,true,icon),"반투명 채움",style.Fill==ShapeFill.Translucent,()=>surface.InkStyle=surface.InkStyle with {Fill=ShapeFill.Translucent});
                else Segment(FillIcon(ShapeFill.Outline,false,icon),"둥근 테두리",style.Fill==ShapeFill.Outline && style.CornerRadius>0,()=>surface.InkStyle=surface.InkStyle with {Fill=ShapeFill.Outline,CornerRadius=rounded});
                Segment(FillIcon(ShapeFill.Outline,oval,icon,square:!oval),"테두리",style.Fill==ShapeFill.Outline && (oval || style.CornerRadius==0),()=>surface.InkStyle=oval?surface.InkStyle with {Fill=ShapeFill.Outline}:surface.InkStyle with {Fill=ShapeFill.Outline,CornerRadius=0});
                Segment(FillIcon(ShapeFill.Hatched,oval,icon),"사선 채움",style.Fill==ShapeFill.Hatched,()=>surface.InkStyle=surface.InkStyle with {Fill=ShapeFill.Hatched});
                Segment(FillIcon(ShapeFill.Solid,oval,icon),"단색 채움",style.Fill==ShapeFill.Solid,()=>surface.InkStyle=surface.InkStyle with {Fill=ShapeFill.Solid});
                Choice(new[]{"테두리","반투명 채움","단색 채움","사선 채움"},(int)surface.InkStyle.Fill,index=>surface.InkStyle=surface.InkStyle with {Fill=(ShapeFill)index});
                var sketch=new CheckBox {Content="손그림",IsChecked=surface.InkStyle.Sketch,Margin=new Thickness(8,8,4,0),ToolTip="살짝 휘어진 선과 부드러운 모서리"};propertyDetails.Children.Add(sketch);
                sketch.Click+=(_,_)=>{surface.InkStyle=surface.InkStyle with {Sketch=sketch.IsChecked==true};surface.ApplyStyle();};
            }
            if(kind==AnnotationKind.Rectangle || kind==AnnotationKind.Text && surface.InkStyle.Bubble)
                ValueSlider("모서리 둥글기",0,48,surface.InkStyle.CornerRadius,value=>surface.InkStyle=surface.InkStyle with {CornerRadius=value});
            if(kind==AnnotationKind.Text) {
                Segment(FillIcon(ShapeFill.Solid,false,icon,bubble:true),"말풍선",surface.InkStyle.Bubble,()=>surface.InkStyle=surface.InkStyle with {Bubble=true});
                Segment(LetterIcon(icon),"일반 글자",!surface.InkStyle.Bubble,()=>surface.InkStyle=surface.InkStyle with {Bubble=false});
                if(a!=null){var edit=ToolButton(propertyDetails,"✎","글자 편집 · 더블클릭",()=>BeginTextEdit(a,false));edit.Height=32;}
            }
            if(kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Step || kind==AnnotationKind.Text && surface.InkStyle.Bubble) {
                var shapeTail=kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse;
                var tail=new CheckBox {Content="말꼬리",IsChecked=shapeTail?surface.InkStyle.ShapeTail:surface.InkStyle.ShowTail,Margin=new Thickness(8,8,4,0),ToolTip="파란 원을 드래그해 방향·길이 조절 · 민트색 마름모로 모서리 조절"};propertyDetails.Children.Add(tail);
                tail.Click+=(_,_)=>{surface.InkStyle=shapeTail?surface.InkStyle with {ShapeTail=tail.IsChecked==true}:surface.InkStyle with {ShowTail=tail.IsChecked==true};surface.ApplyStyle();SyncInspector();};
            }
            if(kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Text && AnnotationGeometry.HasTail(a ?? new Annotation(Guid.NewGuid(),kind.Value,new(0,0),new(80,40),surface.Ink,Style:surface.InkStyle)))
                ValueSlider("말꼬리 너비",8,64,surface.InkStyle.TailWidth,value=>surface.InkStyle=surface.InkStyle with {TailWidth=value});
            if(a?.Kind==AnnotationKind.Step) {
                var value=new TextBox {Text=a.Text,Width=48,Padding=new Thickness(6,4,6,4),Margin=new Thickness(7,5,0,5),ToolTip="번호 0–99 · Enter 적용"};propertyDetails.Children.Add(value);
                System.Windows.Automation.AutomationProperties.SetName(value,"번호");
                value.KeyDown+=(_,e)=>{if(e.Key==Key.Enter && int.TryParse(value.Text,out int n) && n is >=0 and <=99){surface.ChangeSelected(x=>x with {Text=n.ToString()});surface.Focus();e.Handled=true;}};
            }
        } finally {syncingControls=false;}
        // Rarely used controls stay one click away behind ···; tools without compact controls show them directly.
        var compact=propertyRow.Children.Count>1;
        if(compact && propertyDetails.Children.Count>0) {
            var more=ToolButton(propertyRow,"···","추가 속성 · 프리셋·숫자 입력·말꼬리·모드",()=>{inspectorExpanded=!inspectorExpanded;SyncInspector();});
            more.Height=32;more.MinWidth=30;more.FontSize=16;if(inspectorExpanded)more.SetResourceReference(BackgroundProperty,"SelectedToolBrush");
            System.Windows.Automation.AutomationProperties.SetName(more,"속성 더 보기");
        }
        propertyDetails.Visibility=propertyDetails.Children.Count>0 && (inspectorExpanded || !compact)?Visibility.Visible:Visibility.Collapsed;
        void Segment(Drawing drawing,string label,bool selected,Action apply) {
            var button=new Button {Content=new Image {Source=new DrawingImage(drawing),Width=26,Height=20,Stretch=Stretch.Uniform},ToolTip=label,Width=34,Height=32,Padding=new Thickness(4),Margin=new Thickness(1,0,1,0),Style=(Style)Application.Current.FindResource("EditorIconButton")};
            if(selected)button.SetResourceReference(BackgroundProperty,"SelectedToolBrush");else button.Background=Brushes.Transparent;
            System.Windows.Automation.AutomationProperties.SetName(button,label);
            button.Click+=(_,_)=>{apply();surface.ApplyStyle();SyncInspector();};propertyRow.Children.Add(button);
        }
        void ValueSlider(string label,double minimum,double maximum,double value,Action<double> update) {
            var caption=new TextBlock {Text=label.Split('·')[0].Trim(),FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5)};caption.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");propertyDetails.Children.Add(caption);
            var control=new Slider {Minimum=minimum,Maximum=maximum,Value=value,Width=85,Margin=new Thickness(6),ToolTip=label,VerticalAlignment=VerticalAlignment.Center};
            System.Windows.Automation.AutomationProperties.SetName(control,label);propertyDetails.Children.Add(control);
            var readout=NumericField(control,label);propertyDetails.Children.Add(readout);
            control.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>document.BeginEdit()));
            control.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>{document.EndEdit();SyncInspector();}));
            control.ValueChanged+=(_,_)=>{if(syncingControls)return;try{readout.Text=$"{control.Value:0.#}";update(control.Value);surface.ApplyStyle();}catch(Exception error) when(error is InvalidOperationException or ArgumentException){document.CancelEdit();status.Text=error.Message;SyncInspector();}};
            Group(caption,control,readout);
        }
        void Group(params UIElement[] controls) {
            var row=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            foreach(var control in controls){propertyDetails.Children.Remove(control);row.Children.Add(control);}propertyDetails.Children.Add(row);
        }
        void Choice(string[] labels,int selected,Action<int> update) {
            var combo=new ComboBox {ItemsSource=labels,SelectedIndex=selected,MinWidth=90,Margin=new Thickness(4),VerticalAlignment=VerticalAlignment.Center};propertyDetails.Children.Add(combo);
            combo.SelectionChanged+=(_,_)=>{if(syncingControls || combo.SelectedIndex<0)return;update(combo.SelectedIndex);surface.ApplyStyle();};
        }
    }
    /// <summary>Icon for a fill segment, drawn in the panel's text colour on a 24×18 grid.</summary>
    private static Drawing FillIcon(ShapeFill fill,bool oval,ColorRgba ink,bool square=false,bool bubble=false)
    {
        var brush=new SolidColorBrush(Color.FromRgb(ink.R,ink.G,ink.B));var pen=new Pen(brush,1.8);
        var box=new Rect(3,2,18,13);
        Geometry shape=oval?new EllipseGeometry(box):square?new RectangleGeometry(box):new RectangleGeometry(box,4,4);
        var group=new DrawingGroup();
        using(var dc=group.Open()) {
            dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,24,18));
            if(fill==ShapeFill.Hatched) {
                dc.PushClip(shape);
                for(double x=-14;x<24;x+=4.5)dc.DrawLine(new Pen(brush,1.2),new Point(x,17),new Point(x+15,0));
                dc.Pop();
            }
            dc.DrawGeometry(fill is ShapeFill.Solid?brush:fill==ShapeFill.Translucent?new SolidColorBrush(Color.FromArgb(90,ink.R,ink.G,ink.B)):null,fill is ShapeFill.Solid?null:pen,shape);
            if(bubble)dc.DrawGeometry(brush,null,Geometry.Parse("M 7,14 L 7,18 L 11,14 Z"));
        }
        return group;
    }
    private static Drawing LetterIcon(ColorRgba ink)
    {
        var group=new DrawingGroup();
        using(var dc=group.Open()) {
            dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,24,18));
            var text=new FormattedText("T",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal),17,new SolidColorBrush(Color.FromRgb(ink.R,ink.G,ink.B)),1);
            dc.DrawText(text,new Point(12-text.Width/2,9-text.Height/2));
        }
        return group;
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
            button.Click+=(_,_)=>{var size=preset.Size*PresetScale;surface.Ink=preset.Color;surface.Thickness=size;surface.InkStyle=preset.Style;surface.ApplyStyle();toolStyles[kind]=(preset.Color,size,preset.Style);popup.IsOpen=false;SyncInspector();surface.Focus();};panel.Children.Add(button);
        }
        popup.Child=PopupFrame(panel,8);
        KeyboardNavigation.SetTabNavigation(panel,KeyboardNavigationMode.Cycle);
        popup.Opened+=(_,_)=>panel.Children.OfType<Button>().FirstOrDefault()?.Focus();
        panel.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;anchor.Focus();e.Handled=true;}};
        popup.IsOpen=true;
    }
    private static string ToolName(AnnotationKind? kind)=>kind switch {AnnotationKind.Magnifier=>"돋보기",AnnotationKind.Spotlight=>"Spotlight",AnnotationKind.Image=>"추가 이미지",AnnotationKind.Erase=>"Erase",AnnotationKind.Arrow=>"화살표",AnnotationKind.Text=>"텍스트",AnnotationKind.Step=>"번호",AnnotationKind.Rectangle=>"사각형",AnnotationKind.Ellipse=>"타원",AnnotationKind.Pen=>"펜",AnnotationKind.Line=>"선",AnnotationKind.Pixelate=>"모자이크",AnnotationKind.Blur=>"흐림",AnnotationKind.Highlight=>"형광펜",_=>"가림"};
    private void ShowColors(Button anchor)
    {
        var popup=new Popup {PlacementTarget=anchor,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
        var panel=new StackPanel();popup.Child=PopupFrame(panel,10);
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
        textEditor.FontSize=Math.Max(14,a.Thickness*5)*z;textEditor.FontWeight=FontWeights.SemiBold;
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
    private void UpdateCropStatus(){if(surface.PendingCrop is RectD c)status.Text=$"{(surface.CropTool?"자르기":"범위")} {c.Width:0}×{c.Height:0} · 방향키 이동 · Alt+방향키 크기 · [ ] 확장/축소 · A 내용에 맞춤 · Enter 자르기"+(surface.CropTool?"":" · Delete 지우기");}
    /// <summary>Delete on a range box replaces it with an Erase object, like the reference app's raster erase.</summary>
    internal bool EraseRegion()
    {
        if(!surface.RegionActive)return false;
        var style=toolStyles.GetValueOrDefault(AnnotationKind.Erase,(Color:new ColorRgba(240,65,70,255),Size:4d*PresetScale,Style:new AnnotationStyle()));
        if(!surface.ApplyRegionEffect(AnnotationKind.Erase,style.Color,style.Size,style.Style))return false;
        status.Text="범위를 지웠습니다 · 속성에서 채움 방식 변경 · Ctrl+Z 취소";return true;
    }
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
    private void UpdateStatus(){var pad=2*document.Backdrop.PaddingPixels;var k=document.ExportScale;
        dimensions.Text=$"{Math.Round((document.Crop.Width+pad)*k):0}×{Math.Round((document.Crop.Height+pad)*k):0}"+(pad>0?" · 배경":"")+(k<1?$" · {k:P0}":"");}
    private void OnKey(object sender,KeyEventArgs e)
    {
        if(e.OriginalSource is TextBox || textEditor!=null || IsPropertyInput(e.OriginalSource as DependencyObject))return;
        if(e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))FinishNudge();
        var ctrl=Keyboard.Modifiers.HasFlag(ModifierKeys.Control);var shift=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if(surface.RegionActive && !ctrl) {
            // Range precision like the capture overlay, for the crop tool and the select-mode range box: arrows move, Alt+arrows resize, [ ] grow/shrink, A fits to content.
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
            case Key.Tab when e.OriginalSource==surface && hoverColor is ColorRgba hovered:
                var hex=PixelProbe.Hex(hovered);
                try{Clipboard.SetText(hex);status.Text=hex+" 복사됨";}catch(System.Runtime.InteropServices.ExternalException){status.Text=hex+" · 다른 앱이 클립보드를 사용 중입니다";}
                break;
            case Key.Delete:if(!EraseRegion())surface.DeleteSelected();break;
            case Key.Escape:if(surface.Tool is null && !surface.CropTool && !surface.PickColorTool && surface.Selected is null && surface.PendingCrop is null)_=DismissWithEscapeAsync();else SetTool(null);break;
            case Key.Enter:surface.ApplyCrop();break;
            case Key.V:SetTool(null);break;case Key.A:SetTool(AnnotationKind.Arrow);break;case Key.L:SetTool(AnnotationKind.Line);break;
            case Key.R:SetTool(AnnotationKind.Rectangle);break;case Key.O:SetTool(AnnotationKind.Ellipse);break;case Key.P:SetTool(AnnotationKind.Pen);break;
            case Key.T:SetTool(AnnotationKind.Text);break;case Key.N:SetTool(AnnotationKind.Step);break;case Key.H:SetTool(AnnotationKind.Highlight);break;
            case Key.G:SetTool(AnnotationKind.Magnifier);break;case Key.S:SetTool(AnnotationKind.Spotlight);break;case Key.E:SetTool(AnnotationKind.Erase);break;
            case Key.M:SetTool(AnnotationKind.Pixelate);break;case Key.B:SetTool(AnnotationKind.Blur);break;case Key.C:StartCrop();break;case Key.I:StartColorPick();break;
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
            status.Text="편집 이미지를 복사했습니다.";if(revision==copied && hideAfterExport)Hide();
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
        try {ImageActions.Save(BackdropRenderer.Export(source,document),path);savedPath=path;status.Text=$"저장 완료: {path}";if(hideAfterExport)Hide();}
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
