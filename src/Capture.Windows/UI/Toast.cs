using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Capture.Windows.UI;

/// <summary>
/// A short notice card: a colour swatch or icon, a title and an optional second line, and an optional action.
/// Large radius, a soft frosted surface and a deep, soft shadow, light or dark with the system theme.
/// </summary>
internal static class Toast
{
    public static Border Card(string title, string? detail = null, Color? swatch = null, string? icon = null, string? action = null, Action? onAction = null, bool? dark = null)
    {
        bool night = dark ?? Editor.EditorTheme.SystemUsesDark();
        var surface = new LinearGradientBrush(night ? Color.FromArgb(236, 52, 56, 64) : Color.FromArgb(240, 248, 250, 253), night ? Color.FromArgb(236, 40, 43, 50) : Color.FromArgb(240, 232, 238, 246), 90);
        var ink = new SolidColorBrush(night ? Color.FromRgb(245, 246, 248) : Color.FromRgb(28, 30, 34));
        var muted = new SolidColorBrush(night ? Color.FromArgb(175, 255, 255, 255) : Color.FromArgb(165, 28, 30, 34));
        var row = new DockPanel { LastChildFill = true };
        if (swatch is Color color || icon is not null)
        {
            var badge = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(night ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0)) };
            if (swatch is Color fill) badge.Background = new SolidColorBrush(fill);
            else
            {
                badge.Background = new SolidColorBrush(night ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0));
                badge.Child = new ContentControl { Content = Ui.Icon(icon!, 18), Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge);
        }
        if (action is not null)
        {
            var face = new FrameworkElementFactory(typeof(Border));
            face.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); face.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
            face.SetValue(Border.BackgroundProperty, new SolidColorBrush(night ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0)));
            var text = new FrameworkElementFactory(typeof(ContentPresenter)); face.AppendChild(text);
            var button = new Button { Template = new ControlTemplate(typeof(Button)) { VisualTree = face }, Content = new TextBlock { Text = action, Foreground = ink, FontSize = 13, FontFamily = Ui.Font }, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand };
            System.Windows.Automation.AutomationProperties.SetName(button, action);
            button.Click += (_, _) => onAction?.Invoke();
            DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
        }
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(new TextBlock { Text = title, Foreground = ink, FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Font, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(detail)) lines.Children.Add(new TextBlock { Text = detail, Foreground = muted, FontSize = 12.5, FontFamily = Ui.Font, Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        row.Children.Add(lines);
        var card = new Border
        {
            Child = row, Background = surface, CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 10, action is null ? 18 : 10, 10), MinWidth = 220,
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(night ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255)),
            // A wide, soft shadow lifts the card off any screenshot underneath, as system notices do.
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = night ? .5 : .28, Color = Colors.Black },
            Margin = new Thickness(24), IsHitTestVisible = action is not null,
        };
        return card;
    }

    /// <summary>Slides the card in from slightly above and fades it.</summary>
    public static void Animate(FrameworkElement card)
    {
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        card.RenderTransform = new TranslateTransform(0, -10);
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, duration) { EasingFunction = ease });
    }

    /// <summary>Splits "title · detail" text used across the app into the card's two lines.</summary>
    public static (string Title, string? Detail) Lines(string text)
    {
        int split = text.IndexOf(" · ", StringComparison.Ordinal);
        return split < 0 ? (text, null) : (text[..split], text[(split + 3)..]);
    }
}
