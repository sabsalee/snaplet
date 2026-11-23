using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Capture.Core;
using Capture.Windows.UI;

namespace Capture.Windows.Editor;

internal enum LayerMove { Front, Forward, Backward, Back }

/// <summary>The editor's objects listed front to back, docked at the right edge of the editor.
/// A click selects; dragging a row or the buttons at the bottom change the drawing order. Every change is one undo step.</summary>
internal sealed class LayerPanel : Border
{
    internal const double PanelWidth = 248;
    private readonly EditorSurface surface;
    private readonly EditableDocument document;
    private readonly Func<bool> finishEdits;
    private readonly StackPanel rows = new();
    private readonly TextBlock count = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) };
    private readonly TextBlock empty = new() { Text = "아직 그린 객체가 없습니다.\n도형·글자·효과를 넣으면 만든 순서대로 쌓입니다.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 10, 8, 10) };
    private readonly Border insertMark = new() { Height = 2, CornerRadius = new CornerRadius(1), IsHitTestVisible = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
    private readonly Button front, forward, backward, back;
    private string shownSignature = "";
    private Guid? pressed, shownSelection;
    private Point pressPoint;
    private bool dragging;
    private int dropSlot = -1;
    /// <summary>Raised by the close button; the window owns whether the panel is shown.</summary>
    internal event Action? CloseRequested;
    /// <summary>Short confirmation after a change, shown in the editor's status toast.</summary>
    internal event Action<string>? Notice;

    internal LayerPanel(EditorSurface surface, EditableDocument document, Func<bool> finishEdits)
    {
        this.surface = surface; this.document = document; this.finishEdits = finishEdits;
        Width = PanelWidth; BorderThickness = new Thickness(1, 0, 0, 0);
        SetResourceReference(BackgroundProperty, "ToolbarBrush"); SetResourceReference(BorderBrushProperty, "BorderBrush");
        SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(this, "레이어");
        var layout = new DockPanel(); Child = layout;

        var header = new DockPanel { Margin = new Thickness(12, 8, 6, 2) }; DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var close = Ui.IconButton("close", "레이어 패널 닫기 · Ctrl+L", () => CloseRequested?.Invoke(), 30); close.Padding = new Thickness(8);
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var title = new TextBlock { Text = "레이어", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); count.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(count); header.Children.Add(heading);

        var hint = new TextBlock { Text = "위에 있을수록 앞에 보입니다 · 끌어서 순서 변경", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); DockPanel.SetDock(hint, Dock.Top); layout.Children.Add(hint);

        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 6, 8, 8) };
        footer.SetResourceReference(BorderBrushProperty, "BorderBrush"); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var buttons = new UniformGrid { Rows = 1, Columns = 4 }; footer.Child = buttons;
        Button MoveButton(string glyph, string tip, LayerMove move) { var b = Ui.IconButton(glyph, tip, () => Restack(move), 34); b.Width = double.NaN; b.Margin = new Thickness(2, 0, 2, 0); buttons.Children.Add(b); return b; }
        front = MoveButton("bring-front", "맨 앞으로 · Ctrl+Shift+]", LayerMove.Front);
        forward = MoveButton("bring-forward", "앞으로 · Ctrl+]", LayerMove.Forward);
        backward = MoveButton("send-backward", "뒤로 · Ctrl+[", LayerMove.Backward);
        back = MoveButton("send-back", "맨 뒤로 · Ctrl+Shift+[", LayerMove.Back);

        empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        insertMark.SetResourceReference(BackgroundProperty, "AccentBrush");
        var list = new Grid { Background = Brushes.Transparent }; list.Children.Add(rows); list.Children.Add(empty); list.Children.Add(insertMark);
        layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(6, 0, 6, 6) });

        rows.MouseMove += (_, e) => DragTo(e);
        rows.MouseLeftButtonUp += (_, _) => Drop();
        rows.LostMouseCapture += (_, _) => EndDrag();
        document.Changed += (_, _) => Refresh();
        surface.SelectionChanged += Refresh;
        IsVisibleChanged += (_, _) => { shownSignature = ""; Refresh(); };
        Refresh();
    }

    /// <summary>Moves the selected object in the drawing order. Works while the panel is closed, for the keyboard shortcuts.</summary>
    internal bool Restack(LayerMove move)
    {
        if (surface.Selection is not Annotation a || !finishEdits()) return false;
        var from = IndexOf(a.Id); var last = document.Annotations.Count - 1;
        var to = move switch { LayerMove.Front => last, LayerMove.Forward => from + 1, LayerMove.Backward => from - 1, _ => 0 };
        to = Math.Clamp(to, 0, last);
        if (from < 0 || to == from) return false;
        surface.CancelGesture(); document.EndEdit(); document.Reorder(a.Id, to);
        Notice?.Invoke(move switch { LayerMove.Front => "맨 앞으로 옮겼습니다", LayerMove.Forward => "한 단계 앞으로 옮겼습니다", LayerMove.Backward => "한 단계 뒤로 옮겼습니다", _ => "맨 뒤로 옮겼습니다" } + " · Ctrl+Z 취소");
        return true;
    }

    private int IndexOf(Guid id)
    {
        var items = document.Annotations;
        for (int i = 0; i < items.Count; i++) if (items[i].Id == id) return i;
        return -1;
    }

    /// <summary>Rebuilds the rows only when the objects or their labels change; a new selection only repaints the highlight.</summary>
    private void Refresh()
    {
        var items = document.Annotations;
        var selectedIndex = surface.Selected is Guid id ? IndexOf(id) : -1;
        front.IsEnabled = forward.IsEnabled = selectedIndex >= 0 && selectedIndex < items.Count - 1;
        back.IsEnabled = backward.IsEnabled = selectedIndex > 0;
        if (!IsVisible || dragging) return;
        var signature = string.Join("|", items.Select(a => $"{a.Id}:{a.Kind}:{a.Color}:{a.Text}"));
        if (signature != shownSignature)
        {
            shownSignature = signature; rows.Children.Clear();
            for (int i = items.Count - 1; i >= 0; i--) rows.Children.Add(Row(items[i]));
            count.Text = items.Count > 0 ? items.Count.ToString() : "";
            empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            shownSelection = null;
        }
        foreach (Border row in rows.Children) Paint(row);
        if (shownSelection != surface.Selected)
        {
            shownSelection = surface.Selected;
            rows.Children.OfType<Border>().FirstOrDefault(r => r.Tag is Guid g && g == surface.Selected)?.BringIntoView();
        }
    }

    private void Paint(Border row)
    {
        var id = (Guid)row.Tag;
        if (id == surface.Selected) row.SetResourceReference(BackgroundProperty, "SelectedBrush");
        else if (row.IsMouseOver && !dragging) row.SetResourceReference(BackgroundProperty, "HoverBrush");
        else row.Background = Brushes.Transparent;
        if (row.Child is DockPanel { Children.Count: > 0 } line && line.Children[0] is Button remove)
            remove.Visibility = id == surface.Selected || row.IsMouseOver ? Visibility.Visible : Visibility.Hidden;
    }

    private Border Row(Annotation a)
    {
        var id = a.Id;
        var line = new DockPanel { LastChildFill = true };
        var remove = Ui.IconButton("trash", "삭제 · Delete", () => Delete(id), 26); remove.Padding = new Thickness(6); remove.Visibility = Visibility.Hidden;
        DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove);
        var grip = Ui.Grip(); DockPanel.SetDock(grip, Dock.Left); line.Children.Add(grip);
        var glyph = Ui.Icon(Glyph(a.Kind), 16); glyph.Margin = new Thickness(4, 0, 8, 0); glyph.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(glyph, Dock.Left); line.Children.Add(glyph);
        if (a.Kind is not (AnnotationKind.Image or AnnotationKind.Blur or AnnotationKind.Pixelate))
        {
            var swatch = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = ImageRenderer.BrushOf(a.Color with { A = 255 }), StrokeThickness = 1, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            swatch.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "HandleBorderBrush");
            DockPanel.SetDock(swatch, Dock.Left); line.Children.Add(swatch);
        }
        var name = new TextBlock { Text = NameOf(a), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); line.Children.Add(name);
        var row = new Border { Child = line, Tag = id, CornerRadius = new CornerRadius(6), Padding = new Thickness(4, 2, 2, 2), Margin = new Thickness(0, 1, 0, 1), MinHeight = 32, Cursor = Cursors.Hand, ToolTip = NameOf(a) };
        System.Windows.Automation.AutomationProperties.SetName(row, NameOf(a));
        row.MouseEnter += (_, _) => { if (!dragging) surface.ShowHover(id); Paint(row); };
        row.MouseLeave += (_, _) => { if (!dragging) surface.ShowHover(null); Paint(row); };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (!SelectRow(id)) return;
            pressed = id; pressPoint = e.GetPosition(rows); rows.CaptureMouse(); e.Handled = true;
        };
        return row;
    }

    /// <summary>Selects an object as a click on its row does.</summary>
    internal bool SelectRow(Guid id)
    {
        if (!finishEdits()) return false;
        document.EndEdit(); surface.Select(id); surface.Focus(); return true;
    }
    /// <summary>Row labels from the top of the list, for tests.</summary>
    internal IReadOnlyList<string> RowNames => rows.Children.OfType<Border>().Select(r => r.ToolTip?.ToString() ?? "").ToArray();

    private void Delete(Guid id)
    {
        if (!finishEdits()) return;
        surface.CancelGesture(); document.EndEdit(); surface.ShowHover(null);
        if (surface.Selected == id) surface.DeleteSelected(); else document.Delete(id);
        Notice?.Invoke("삭제했습니다 · Ctrl+Z 취소");
    }

    private void DragTo(MouseEventArgs e)
    {
        if (pressed is null || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(rows);
        if (!dragging)
        {
            if (Math.Abs(at.Y - pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragging = true; surface.ShowHover(null); Cursor = Cursors.SizeNS;
            foreach (Border row in rows.Children) Paint(row);
        }
        // The slot is the gap before the row under the pointer's upper half, counted from the top of the list.
        dropSlot = rows.Children.Count;
        double markY = 0;
        for (int i = 0; i < rows.Children.Count; i++)
        {
            var row = (Border)rows.Children[i];
            var top = row.TranslatePoint(new Point(0, 0), rows).Y;
            markY = top + row.ActualHeight + 1;
            if (at.Y < top + row.ActualHeight / 2) { dropSlot = i; markY = top - 1; break; }
        }
        insertMark.Margin = new Thickness(6, Math.Max(0, markY - 1), 6, 0);
        insertMark.Visibility = Visibility.Visible;
    }

    private void Drop()
    {
        if (pressed is Guid id && dragging && dropSlot >= 0)
        {
            var total = document.Annotations.Count; var index = IndexOf(id);
            var fromRow = total - 1 - index;
            var toRow = dropSlot > fromRow ? dropSlot - 1 : dropSlot;
            EndDrag();
            if (index >= 0 && toRow != fromRow && finishEdits()) { document.Reorder(id, total - 1 - toRow); Notice?.Invoke("순서를 바꿨습니다 · Ctrl+Z 취소"); }
        }
        EndDrag();
    }

    private void EndDrag()
    {
        var wasDragging = dragging;
        pressed = null; dragging = false; dropSlot = -1; insertMark.Visibility = Visibility.Collapsed; ClearValue(CursorProperty);
        if (rows.IsMouseCaptured) rows.ReleaseMouseCapture();
        if (wasDragging) Refresh();
    }

    internal static string Glyph(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Arrow => "arrow", AnnotationKind.Line => "line", AnnotationKind.Rectangle => "rectangle", AnnotationKind.Ellipse => "ellipse",
        AnnotationKind.Pen => "pen", AnnotationKind.Text => "text", AnnotationKind.Step => "step", AnnotationKind.Highlight => "highlight",
        AnnotationKind.Pixelate => "pixelate", AnnotationKind.Blur => "blur", AnnotationKind.Magnifier => "lens", AnnotationKind.Spotlight => "spotlight",
        AnnotationKind.Image => "image", _ => "redact",
    };

    /// <summary>A row label: the kind, plus what tells two objects of that kind apart.</summary>
    internal static string NameOf(Annotation a)
    {
        string Snippet(string text) { var first = text.Replace("\r", "").Split('\n')[0].Trim(); return first.Length > 24 ? first[..24] + "…" : first; }
        return a.Kind switch
        {
            AnnotationKind.Text => string.IsNullOrWhiteSpace(a.Text) ? "글자" : "글자 · " + Snippet(a.Text),
            AnnotationKind.Step => string.IsNullOrWhiteSpace(a.Text) ? "번호" : "번호 " + Snippet(a.Text),
            AnnotationKind.Image => a.Image is RasterImage image ? $"이미지 · {image.Width}×{image.Height}" : "이미지",
            AnnotationKind.Arrow => "화살표", AnnotationKind.Line => "선", AnnotationKind.Rectangle => "사각형", AnnotationKind.Ellipse => "타원",
            AnnotationKind.Pen => "펜", AnnotationKind.Highlight => "형광펜", AnnotationKind.Pixelate => "모자이크", AnnotationKind.Blur => "흐림",
            AnnotationKind.Magnifier => "돋보기", AnnotationKind.Spotlight => "Spotlight", _ => "가림",
        };
    }
}
