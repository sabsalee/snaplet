using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Capture.Windows.UI;

/// <summary>
/// Folds the title bar into the window, the way modern Windows 11 apps do: the window's own top band (a toolbar,
/// or a slim strip with the title) becomes the drag area, Windows keeps drawing its minimize, maximize and close
/// buttons (so snap layouts still work), and the band shows a light Mica backdrop under a translucent tint.
/// On builds before Windows 11 22H2, where the system backdrop is unavailable, the window keeps its normal frame.
/// </summary>
internal static class WindowFrame
{
    /// <summary>Space the system caption buttons take at the top right of a unified window.</summary>
    internal const double CaptionButtons = 144;
    /// <summary>Height of the slim title strip for windows that have no toolbar of their own.</summary>
    internal const double Strip = 40;
    /// <summary>Test hook: force the unified frame on or off regardless of the Windows build.</summary>
    internal static bool? Override { get; set; }
    internal static bool Supported => Override ?? Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// Makes <paramref name="band"/> the window's title area. Its children stay clickable; the band's own empty space
    /// drags the window and double-clicking it maximizes. Room is kept at the right for the system caption buttons.
    /// Returns false (and changes nothing) when the unified frame is not supported.
    /// With <paramref name="captionButtons"/>, Windows draws no caption buttons at all and the given element (the
    /// window's own close button) takes their place at the far right; double-clicking the band still maximizes,
    /// dragging to a screen edge still snaps, and the taskbar button still minimizes.
    /// </summary>
    internal static bool Unify(Window window, DockPanel band, double height, FrameworkElement? captionButtons = null)
    {
        if (!Supported) return false;
        var custom = captionButtons is not null;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = height, GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = !custom,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness, CornerRadius = new CornerRadius(0), NonClientFrameEdges = NonClientFrameEdges.None,
        });
        window.Background = Brushes.Transparent;
        // The band is mostly opaque (TitleBandBrush) so the backdrop only softly shows through.
        band.SetResourceReference(Panel.BackgroundProperty, "TitleBandBrush");
        band.Tag = Marker;
        foreach (UIElement child in band.Children) Interactive(child);
        var room = captionButtons ?? new FrameworkElement { Width = CaptionButtons }; DockPanel.SetDock(room, Dock.Right); band.Children.Insert(0, room);
        if (custom) Interactive(room);
        void Backdrop()
        {
            var hwnd = new WindowInteropHelper(window).Handle; if (hwnd == 0) return;
            if (custom) HideCaptionButtons(hwnd);
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            int mica = 2; DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int));
        }
        if (new WindowInteropHelper(window).Handle != 0) Backdrop(); else window.SourceInitialized += (_, _) => Backdrop();
        // A maximized window hangs its resize frame off the screen edges; pad the content back inside.
        void Fit()
        {
            if (window.Content is not FrameworkElement content) return;
            content.Margin = window.WindowState == WindowState.Maximized ? MaximizedInset(window) : new Thickness(0);
        }
        window.StateChanged += (_, _) => Fit(); window.Loaded += (_, _) => Fit();
        return true;
    }

    private const int GwlStyle = -16, WmStyleChanging = 0x007C;
    private const long WsSysMenu = 0x00080000;
    /// <summary>Whether Windows has been told to draw no caption buttons on this window (for tests).</summary>
    internal static bool HasCustomCaption(Window window) => WindowChrome.GetWindowChrome(window) is { UseAeroCaptionButtons: false };

    /// <summary>
    /// The extended frame draws minimize, maximize and close only for a window with a system menu, so dropping
    /// WS_SYSMENU removes all three. WPF can put the style back when it refreshes the window; a hook strips it again.
    /// Minimize and maximize stay in the style, so the taskbar button, Win+arrow keys and edge snapping keep working.
    /// </summary>
    private static void HideCaptionButtons(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        if ((style & (nint)WsSysMenu) != 0) { SetWindowLongPtr(hwnd, GwlStyle, style & ~(nint)WsSysMenu); SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); }
        HwndSource.FromHwnd(hwnd)?.AddHook((nint _, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == WmStyleChanging && (int)wParam == GwlStyle)
            {
                var next = (uint)Marshal.ReadInt32(lParam, 4); // STYLESTRUCT.styleNew
                if ((next & WsSysMenu) != 0) Marshal.WriteInt32(lParam, 4, (int)(next & ~(uint)WsSysMenu));
            }
            return 0;
        });
    }

    /// <summary>Marks controls inside a title band as clickable rather than part of the drag area.</summary>
    internal static void Interactive(params UIElement[] elements) { foreach (var element in elements) WindowChrome.SetIsHitTestVisibleInChrome(element, true); }

    /// <summary>
    /// For windows without a toolbar: puts a slim title strip above the current content and unifies the frame with it.
    /// The strip shows the window title in small muted text; nothing else lives there.
    /// </summary>
    internal static bool UnifyWithStrip(Window window)
    {
        if (!Supported || window.Content is not UIElement content) return false;
        var root = new DockPanel();
        var strip = new DockPanel { Height = Strip, LastChildFill = true };
        var title = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Window.Title)) { Source = window });
        title.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        strip.Children.Add(title);
        DockPanel.SetDock(strip, Dock.Top);
        window.Content = null; root.Children.Add(strip); root.Children.Add(content); window.Content = root;
        if (content is FrameworkElement element && GetBackground(element) is null) SetBackground(element);
        return Unify(window, strip, Strip);
    }

    /// <summary>Whether a panel is a unified title band (for tests).</summary>
    internal static bool IsTitleBand(FrameworkElement element) => ReferenceEquals(element.Tag, Marker);
    private static readonly object Marker = new();

    private static Brush? GetBackground(FrameworkElement element) => element switch { Panel p => p.Background, Border b => b.Background, Control c => c.Background, _ => null };
    private static void SetBackground(FrameworkElement element)
    {
        switch (element) { case Panel p: p.SetResourceReference(Panel.BackgroundProperty, "PageBrush"); break; case Border b: b.SetResourceReference(Border.BackgroundProperty, "PageBrush"); break; case Control c: c.SetResourceReference(Control.BackgroundProperty, "PageBrush"); break; }
    }

    private static Thickness MaximizedInset(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        uint dpi = hwnd == 0 ? 96u : GetDpiForWindow(hwnd); if (dpi == 0) dpi = 96;
        double pixels = GetSystemMetricsForDpi(32, dpi) + GetSystemMetricsForDpi(92, dpi); // SM_CXFRAME + SM_CXPADDEDBORDER
        double inset = pixels * 96.0 / dpi;
        return new Thickness(inset);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
