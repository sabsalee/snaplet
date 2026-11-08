using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;
using Capture.Windows.Editor;
using Capture.Windows.UI;

namespace Capture.Windows;

/// <summary>One past capture: in memory, on disk, or both. Disk items load their pixels only when opened.</summary>
internal sealed record GalleryItem(DateTime When,string? Path,BitmapSource? Memory)
{
    internal BitmapSource Open()=>Memory ?? ImageActions.Load(Path!);
}

/// <summary>Every kept capture by date. Click edits; the context menu copies, saves or deletes.</summary>
internal sealed class GalleryWindow : Window
{
    private const double CardWidth=216,CardHeight=136;
    private readonly Func<IReadOnlyList<GalleryItem>> source;
    private readonly Action<BitmapSource> edit;
    private readonly Action<GalleryItem> delete;
    private readonly Action clearAll;
    private readonly Action openSettings;
    private readonly Func<bool> keptOnDisk;
    private readonly bool dark;
    private readonly StackPanel list=new() {Margin=new Thickness(20,6,20,20)};
    private readonly TextBlock count=new() {FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,2,0,0)};
    private readonly Border notice=new() {Padding=new Thickness(24,12,16,0)};
    private readonly Button clear;
    private CancellationTokenSource? loading;
    internal Task ThumbnailsLoaded {get;private set;}=Task.CompletedTask;
    internal int CardCount {get;private set;}
    internal IReadOnlyList<string> Headings {get;private set;}=[];

    internal GalleryWindow(Func<IReadOnlyList<GalleryItem>> items,Action<BitmapSource> editImage,Action<GalleryItem> deleteItem,Action deleteAll,Action settings,Func<bool> onDisk)
    {
        source=items;edit=editImage;delete=deleteItem;clearAll=deleteAll;openSettings=settings;keptOnDisk=onDisk;
        Title="Snaplet · 캡처 기록";Width=984;Height=680;MinWidth=520;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        dark=EditorTheme.SystemUsesDark();EditorTheme.Themed(this,dark);WindowAppearance.SetDarkChrome(this,dark);WindowAppearance.Apply(this,dark);
        SetResourceReference(BackgroundProperty,"PageBrush");SetResourceReference(ForegroundProperty,"TextBrush");FontFamily=Ui.Font;
        var root=new DockPanel();Content=root;

        var toolbar=new DockPanel {LastChildFill=false,Height=52};toolbar.SetResourceReference(Panel.BackgroundProperty,"ToolbarBrush");
        var frame=new Border {Child=toolbar,BorderThickness=new Thickness(0,0,0,1)};frame.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");DockPanel.SetDock(frame,Dock.Top);root.Children.Add(frame);
        var heading=new TextBlock {Text="캡처 기록",FontSize=16,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(20,0,0,0)};DockPanel.SetDock(heading,Dock.Left);toolbar.Children.Add(heading);
        count.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");DockPanel.SetDock(count,Dock.Left);toolbar.Children.Add(count);
        clear=new Button {Content="모두 지우기",Margin=new Thickness(0,0,12,0),Padding=new Thickness(10,4,10,5),VerticalAlignment=VerticalAlignment.Center,Style=(Style)Application.Current.FindResource("QuietButton")};
        clear.SetResourceReference(ForegroundProperty,"TextBrush");clear.Click+=(_,_)=>clearAll();DockPanel.SetDock(clear,Dock.Right);toolbar.Children.Add(clear);

        notice.SetResourceReference(Border.BackgroundProperty,"WorkspaceBrush");DockPanel.SetDock(notice,Dock.Top);root.Children.Add(notice);
        var noticeRow=new DockPanel();notice.Child=noticeRow;
        var turnOn=Ui.Link("설정 열기",()=>openSettings());turnOn.Margin=new Thickness(6,0,0,0);noticeRow.HorizontalAlignment=HorizontalAlignment.Left;DockPanel.SetDock(turnOn,Dock.Right);noticeRow.Children.Add(turnOn);
        var noticeText=new TextBlock {Text=$"앱을 닫으면 사라집니다. 설정 → 저장에서 디스크 보관을 켜면 최근 {HistoryStore.DiskLimit}개를 남깁니다.",FontSize=12,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
        noticeText.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");noticeRow.Children.Add(noticeText);

        var scroll=new ScrollViewer {Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        scroll.SetResourceReference(BackgroundProperty,"WorkspaceBrush");root.Children.Add(scroll);
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        Closed+=(_,_)=>loading?.Cancel();
        Reload();
    }

    /// <summary>Rebuilds from the current captures, keeping thumbnails off the UI thread.</summary>
    internal void Reload()
    {
        loading?.Cancel();loading=new CancellationTokenSource();
        var items=source();var now=DateTime.Now;
        list.Children.Clear();CardCount=items.Count;
        notice.Visibility=keptOnDisk()?Visibility.Collapsed:Visibility.Visible;
        clear.Visibility=items.Count>0?Visibility.Visible:Visibility.Collapsed;
        count.Text=items.Count>0?$"{items.Count}개":"";
        if(items.Count==0) {
            var empty=new TextBlock {Text="아직 캡처가 없습니다",FontSize=14,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,140,0,0)};
            empty.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");list.Children.Add(empty);Headings=[];ThumbnailsLoaded=Task.CompletedTask;return;
        }
        var pending=new List<(string Path,Image Image,TextBlock Size)>();var headings=new List<string>();
        foreach(var group in items.OrderByDescending(i=>i.When).GroupBy(i=>HistoryNames.Heading(i.When,now))) {
            headings.Add(group.Key);
            var title=new TextBlock {Text=group.Key,FontSize=13,FontWeight=FontWeights.SemiBold,Margin=new Thickness(4,16,0,6)};list.Children.Add(title);
            var row=new WrapPanel();list.Children.Add(row);
            foreach(var item in group)row.Children.Add(Card(item,pending));
        }
        Headings=headings;
        ThumbnailsLoaded=LoadThumbnailsAsync(pending,loading.Token);
    }
    private Button Card(GalleryItem item,List<(string,Image,TextBlock)> pending)
    {
        var image=new Image {Width=CardWidth,Height=CardHeight,Stretch=Stretch.UniformToFill,Source=item.Memory};
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        var size=new TextBlock {FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis};size.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
        size.Text=item.Memory is BitmapSource m?Caption(item,m.PixelWidth,m.PixelHeight):item.When.ToString("HH:mm");
        if(item.Memory is null)pending.Add((item.Path!,image,size));
        image.Clip=new RectangleGeometry(new Rect(0,0,CardWidth,CardHeight),6,6);
        var plate=new Border {Child=image,CornerRadius=new CornerRadius(7),BorderThickness=new Thickness(1)};plate.SetResourceReference(Border.BorderBrushProperty,"DividerBrush");
        var stack=new StackPanel();stack.Children.Add(plate);size.Margin=new Thickness(3,5,0,0);stack.Children.Add(size);
        var card=new Button {Content=stack,Margin=new Thickness(0,0,8,8),Padding=new Thickness(4),HorizontalContentAlignment=HorizontalAlignment.Left,Style=(Style)Application.Current.FindResource("QuietButton"),ToolTip="클릭하여 편집 · 오른쪽 클릭으로 복사·저장·삭제"};
        ToolTipService.SetInitialShowDelay(card,1500);
        System.Windows.Automation.AutomationProperties.SetName(card,"캡처 "+item.When.ToString("HH:mm"));
        card.Click+=(_,_)=>Use(item,edit);
        card.KeyDown+=(_,e)=>{if(e.Key==Key.Delete){delete(item);e.Handled=true;}};
        card.ContextMenu=Menu(item);
        return card;
    }
    private ContextMenu Menu(GalleryItem item)
    {
        var menu=EditorTheme.Themed(new ContextMenu(),dark);
        void Add(string text,Action action){var entry=new MenuItem {Header=text};entry.Click+=(_,_)=>action();menu.Items.Add(entry);}
        Add("편집",()=>Use(item,edit));
        Add("복사",()=>Use(item,image=>_=ImageActions.CopyAsync(image)));
        Add("다른 이름으로 저장…",()=>Use(item,image=>ImageActions.SaveDialog(image,this)));
        menu.Items.Add(new Separator());
        Add("삭제 · Delete",()=>delete(item));
        return menu;
    }
    private void Use(GalleryItem item,Action<BitmapSource> action)
    {
        try{action(item.Open());}
        catch(Exception error) when(error is IOException or NotSupportedException or ArgumentException or InvalidOperationException or UnauthorizedAccessException){NoticeWindow.Alert(this,"캡처를 열지 못했습니다",error.Message);Reload();}
    }
    private static string Caption(GalleryItem item,int width,int height)=>$"{item.When:HH:mm} · {width} × {height}";
    private static async Task LoadThumbnailsAsync(List<(string Path,Image Image,TextBlock Size)> pending,CancellationToken token)
    {
        foreach(var (path,image,size) in pending) {
            try {
                var thumbnail=await Task.Run(()=>HistoryStore.Thumbnail(path,(int)CardWidth*2),token);
                if(token.IsCancellationRequested)return;
                image.Source=thumbnail.Image;size.Text+=$" · {thumbnail.Width} × {thumbnail.Height}";
            }
            catch(OperationCanceledException){return;}
            catch(Exception error) when(error is IOException or NotSupportedException or ArgumentException or FileFormatException or UnauthorizedAccessException){size.Text+=" · 읽을 수 없음";}
        }
    }
}
