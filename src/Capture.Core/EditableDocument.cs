using System.Collections.ObjectModel;

namespace Capture.Core;

public enum AnnotationKind { Arrow, Line, Rectangle, Ellipse, Pen, Text, Step, Highlight, Pixelate, Blur, Solid, Magnifier, Spotlight, Image, Erase }
public enum MarkerShape { Line, Freehand, Rectangle }
public enum MarkerCap { Flat, Round, Sketch }
public enum ArrowHead { Filled, Open, Simple, Double }
public enum ShapeFill { Outline, Translucent, Solid, Hatched }
public sealed record AnnotationStyle(ArrowHead ArrowHead = ArrowHead.Filled, ShapeFill Fill = ShapeFill.Outline,
    bool Bubble = true, bool ShowTail = true, double Opacity = 1, bool Sketch = false, bool ShapeTail = false, double Magnification = 3, double Dim = .6, bool Elliptical = false,
    MarkerShape HighlightShape = MarkerShape.Line, MarkerCap MarkerCap = MarkerCap.Flat, double CornerRadius = 8, double TailWidth = 24);

public sealed record Annotation(Guid Id, AnnotationKind Kind, PointD Start, PointD End, ColorRgba Color,
    double Thickness = 4, string Text = "", IReadOnlyList<PointD>? Points = null,
    PointD? Control = null, PointD? Tail = null, AnnotationStyle? Style = null,
    RasterImage? Image = null, RectD? SampleRegion = null)
{
    public AnnotationStyle Appearance => Style ?? new();
    public RectD Bounds
    {
        get
        {
            if (Points is not { Count: > 0 }) return RectD.FromPoints(Start, End);
            return new(Points.Min(p => p.X), Points.Min(p => p.Y), Points.Max(p => p.X) - Points.Min(p => p.X), Points.Max(p => p.Y) - Points.Min(p => p.Y));
        }
    }
    public Annotation Move(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) throw new ArgumentOutOfRangeException(nameof(dx));
        return this with { Start = new(Start.X + dx, Start.Y + dy), End = new(End.X + dx, End.Y + dy),
            Control = Control is PointD c ? new(c.X + dx, c.Y + dy) : null,
            Tail = Tail is PointD t ? new(t.X + dx, t.Y + dy) : null,
            Points = Points is null ? null : Array.AsReadOnly(Points.Select(p => new PointD(p.X + dx, p.Y + dy)).ToArray()) };
    }
}

/// <summary>Non-destructive edits in original pixel coordinates. History holds vectors and crop only, never image copies.</summary>
public sealed class EditableDocument
{
    private const int HistoryLimit = 128;
    private sealed record State(RectD Canvas, RectD Crop, ReadOnlyCollection<Annotation> Annotations, int NextCounter = 1);
    private State state;
    private readonly List<State> undo = [];
    private readonly List<State> redo = [];
    private State? editStart;
    public int Width { get; }
    public int Height { get; }
    public RectD Crop => state.Crop;
    public RectD Canvas => state.Canvas;
    public IReadOnlyList<Annotation> Annotations => state.Annotations;
    public bool IsEditing => editStart is not null;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public int NextCounter => state.NextCounter;
    public long RetainedImageBytes => ImageBytes(state.Annotations.Concat(undo.SelectMany(s=>s.Annotations)).Concat(redo.SelectMany(s=>s.Annotations)));
    public event EventHandler? Changed;
    public void BeginEdit() { editStart ??= state; }
    public void EndEdit()
    {
        if (editStart is not State before) return;
        editStart = null;
        if (before == state) return;
        PushUndo(before); redo.Clear(); TrimImageHistory(); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void CancelEdit()
    {
        if (editStart is not State before) return;
        editStart = null; state = before; Changed?.Invoke(this, EventArgs.Empty);
    }

    public EditableDocument(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height;
        state = new(new(0, 0, width, height), new(0,0,width,height), Array.AsReadOnly(Array.Empty<Annotation>()));
    }
    public void Add(Annotation annotation)
    {
        var frozen = Freeze(annotation);
        if (state.Annotations.Any(a => a.Id == annotation.Id)) throw new ArgumentException("Annotation ID already exists.", nameof(annotation));
        Commit(state with { Annotations = Array.AsReadOnly(state.Annotations.Append(frozen).ToArray()),
            NextCounter = CounterAfter(frozen, state.NextCounter) });
    }
    public void Replace(Annotation annotation)
    {
        var frozen = Freeze(annotation);
        int index = state.Annotations.ToList().FindIndex(a => a.Id == annotation.Id);
        if (index < 0) return;
        if (Equivalent(state.Annotations[index], frozen)) return;
        var items = state.Annotations.ToArray(); items[index] = frozen;
        Commit(state with { Annotations = Array.AsReadOnly(items),
            NextCounter = state.Annotations[index].Text != frozen.Text ? CounterAfter(frozen, state.NextCounter) : state.NextCounter });
    }
    public void Delete(Guid id)
    {
        var items = state.Annotations.Where(a => a.Id != id).ToArray();
        if (items.Length != state.Annotations.Count) Commit(state with { Annotations = Array.AsReadOnly(items) });
    }
    public void CropTo(RectD rect)
    {
        var clipped = rect.Intersect(Canvas);
        if (clipped.IsEmpty) throw new ArgumentException("Crop must intersect the image and have positive area.", nameof(rect));
        if (clipped != Crop) Commit(state with { Crop = clipped });
    }
    public void ResetCrop() => CropTo(Canvas);
    public void ExpandToInclude(RectD bounds)
    {
        if(bounds.IsEmpty)throw new ArgumentException("Invalid canvas bounds.");
        RectD Union(RectD a,RectD b) {var x=Math.Min(a.X,b.X);var y=Math.Min(a.Y,b.Y);return new(x,y,Math.Max(a.Right,b.Right)-x,Math.Max(a.Bottom,b.Bottom)-y);}
        var canvas=Union(Canvas,bounds);
        if(canvas.Width>32768 || canvas.Height>32768 || canvas.Width*canvas.Height>64_000_000)
            throw new InvalidOperationException("Canvas exceeds 64 million pixels or 32768 pixels per side.");
        if(canvas!=Canvas || !Crop.Contains(new(bounds.X,bounds.Y)) || !Crop.Contains(new(bounds.Right,bounds.Bottom)))
            Commit(state with {Canvas=canvas,Crop=Union(Crop,bounds)});
    }
    public void Undo() { EndEdit(); if (!CanUndo) return; redo.Add(state); state = Pop(undo); Changed?.Invoke(this, EventArgs.Empty); }
    public void Redo() { EndEdit(); if (!CanRedo) return; undo.Add(state); state = Pop(redo); Changed?.Invoke(this, EventArgs.Empty); }
    private void Commit(State next)
    {
        if(ImageBytes(next.Annotations)>256_000_000)throw new InvalidOperationException("문서 이미지 한도 256MB를 초과합니다.");
        if (editStart is null) { PushUndo(state); redo.Clear(); }
        state = next;TrimImageHistory(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private static long ImageBytes(IEnumerable<Annotation> annotations)=>annotations.Where(a=>a.Image!=null).Select(a=>a.Image!).Distinct().Sum(a=>(long)a.Width*a.Height*4);
    private void TrimImageHistory()
    {
        while(ImageBytes(state.Annotations.Concat(undo.SelectMany(s=>s.Annotations)).Concat(redo.SelectMany(s=>s.Annotations)))>256_000_000) {
            if(undo.Count>0)undo.RemoveAt(0);else if(redo.Count>0)redo.RemoveAt(0);else break;
        }
    }
    private void PushUndo(State value) { undo.Add(value); if (undo.Count > HistoryLimit) undo.RemoveAt(0); }
    private static State Pop(List<State> list) { var last = list[^1]; list.RemoveAt(list.Count - 1); return last; }
    private static int CounterAfter(Annotation a, int current) => a.Kind == AnnotationKind.Step && int.TryParse(a.Text, out int value) ? Math.Clamp(value + 1, 0, 99) : current;
    private static bool Equivalent(Annotation a, Annotation b) => a.Id == b.Id && a.Kind == b.Kind && a.Start == b.Start && a.End == b.End && a.Color == b.Color && a.Thickness == b.Thickness && a.Text == b.Text && a.Control == b.Control && a.Tail == b.Tail && a.Appearance == b.Appearance && a.Image == b.Image && a.SampleRegion == b.SampleRegion && (a.Points ?? []).SequenceEqual(b.Points ?? []);
    private static Annotation Freeze(Annotation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Id == Guid.Empty || !Enum.IsDefined(value.Kind) || !value.Start.IsFinite || !value.End.IsFinite || !double.IsFinite(value.Thickness) || value.Thickness <= 0 || value.Text is null || (value.Points?.Any(p => !p.IsFinite) ?? false) ||
            (value.Control is PointD c && !c.IsFinite) || (value.Tail is PointD t && !t.IsFinite) ||
            (value.Kind==AnnotationKind.Image && value.Image is null) || (value.SampleRegion is RectD sample && sample.IsEmpty) ||
            !double.IsFinite(value.Appearance.Magnification) || value.Appearance.Magnification is < 1 or > 8 ||
            !double.IsFinite(value.Appearance.CornerRadius) || value.Appearance.CornerRadius is < 0 or > 48 ||
            !double.IsFinite(value.Appearance.TailWidth) || value.Appearance.TailWidth is < 8 or > 64 ||
            !double.IsFinite(value.Appearance.Dim) || value.Appearance.Dim is < 0 or > 1 ||
            !Enum.IsDefined(value.Appearance.HighlightShape) || !Enum.IsDefined(value.Appearance.MarkerCap) ||
            !Enum.IsDefined(value.Appearance.ArrowHead) || !Enum.IsDefined(value.Appearance.Fill) || !double.IsFinite(value.Appearance.Opacity) || value.Appearance.Opacity is < 0 or > 1)
            throw new ArgumentException("Annotation contains invalid geometry, style, or ID.", nameof(value));
        return value with { Points = value.Points is null ? null : Array.AsReadOnly(value.Points.ToArray()) };
    }
}
