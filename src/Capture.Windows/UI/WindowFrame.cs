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
    /// </summary>
    internal static bool Unify(Window window, DockPanel band, double height)
    {
        if (!Supported) return false;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = height, GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = true,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness, CornerRadius = new CornerRadius(0), NonClientFrameEdges = NonClientFrameEdges.None,
        });
        window.Background = Brushes.Transparent;
        // The band is mostly opaque (TitleBandBrush) so the backdrop only softly shows through.
        band.SetResourceReference(Panel.BackgroundProperty, "TitleBandBrush");
        band.Tag = Marker;
        foreach (UIElement child in band.Children) Interactive(child);
        var room = new FrameworkElement { Width = CaptionButtons }; DockPanel.SetDock(room, Dock.Right); band.Children.Insert(0, room);
        void Backdrop()
        {
            var hwnd = new WindowInteropHelper(window).Handle; if (hwnd == 0) return;
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

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
