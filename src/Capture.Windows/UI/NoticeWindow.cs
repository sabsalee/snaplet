using System.Windows;
using System.Windows.Controls;

namespace Capture.Windows.UI;

internal sealed class NoticeWindow : Window
{
    internal NoticeWindow(string title,string message,string? subtitle=null)
    {
        Title="Snaplet";Width=480;MinWidth=380;MaxHeight=620;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var root=new StackPanel();Content=new Border{Background=Ui.Page,Padding=new Thickness(24),Child=root};
        root.Children.Add(Ui.Header(title,subtitle ?? "입력과 화면 상태를 확인한 뒤 다시 시도해 주세요."));
        var text=Ui.Label(message,13,Ui.Muted);root.Children.Add(new ScrollViewer{Content=text,MaxHeight=330,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var close=Ui.Button("확인",Close,style:"PrimaryButton");close.IsDefault=true;close.IsCancel=true;close.HorizontalAlignment=HorizontalAlignment.Right;close.MinWidth=86;close.Margin=new Thickness(0,24,0,0);root.Children.Add(close);
    }
    internal static void Alert(Window? owner,string title,string message)
    {
        var dialog=new NoticeWindow(title,message);if(owner is {IsVisible:true}){dialog.Owner=owner;dialog.WindowStartupLocation=WindowStartupLocation.CenterOwner;}
        dialog.ShowDialog();
    }
    internal static bool Confirm(Window? owner,string title,string message,string action)
    {
        var dialog=new NoticeWindow(title,message,"필요한 이미지를 먼저 저장하고 준비됐을 때 계속하세요.");
        var root=(StackPanel)((Border)dialog.Content).Child;root.Children.RemoveAt(root.Children.Count-1);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,24,0,0)};
        var cancel=Ui.Button("아직 하지 않기",()=>dialog.DialogResult=false,style:"QuietButton");cancel.IsCancel=true;cancel.IsDefault=true;cancel.Margin=new Thickness(0,0,8,0);buttons.Children.Add(cancel);
        buttons.Children.Add(Ui.Button(action,()=>dialog.DialogResult=true,style:"PrimaryButton"));root.Children.Add(buttons);
        if(owner is {IsVisible:true}){dialog.Owner=owner;dialog.WindowStartupLocation=WindowStartupLocation.CenterOwner;}
        return dialog.ShowDialog()==true;
    }
}
