using Capture.Core;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
void Throws(Action action, string message) { try { action(); } catch (ArgumentException) { passed++; return; } throw new Exception(message); }
Annotation Item() => new(Guid.NewGuid(), AnnotationKind.Arrow, new(1, 2), new(30, 40), new(255, 0, 0));

var document = new EditableDocument(100, 80);
int changes = 0; document.Changed += (_, _) => changes++;
document.CropTo(new(-20, -10, 60, 40));
Check(document.Crop == new RectD(0, 0, 40, 30), "Negative crop must clip to original image.");
document.Undo(); Check(document.Crop == new RectD(0, 0, 100, 80), "Undo crop.");
document.Redo(); Check(document.Crop.Width == 40, "Redo crop.");
document.ResetCrop(); Check(document.Crop.Width == 100, "Restore source extent.");
int priorChanges = changes;
Throws(() => document.CropTo(new(200, 200, 5, 5)), "Empty crop rejected.");
Throws(() => document.CropTo(new(double.NaN, 0, 5, 5)), "Nonfinite crop rejected.");
document.Delete(Guid.NewGuid()); document.Replace(Item());
Check(changes == priorChanges, "Invalid/no-op changes must not create history or events.");
var points = new[] { new PointD(2, 3), new PointD(4, 7) };
var pen = Item() with { Kind = AnnotationKind.Pen, Points = points };
document.Add(pen); points[0] = new(999, 999);
Check(document.Annotations[0].Points![0] == new PointD(2, 3), "Document must own points.");
Check(document.Annotations is not Annotation[], "Document must hide mutable array.");
Check(document.Annotations[0].Points is not PointD[], "Points must hide mutable array.");
var moved = document.Annotations[0].Move(-5, 6);
Check(moved.Points![1] == new PointD(-1, 13), "Move pen geometry.");
document.Replace(moved); document.Undo(); Check(document.Annotations[0].Points![1] == new PointD(4, 7), "Undo replacement.");
document.Add(Item()); Check(!document.CanRedo, "New edit invalidates redo branch.");
Throws(() => document.Add(Item() with { Thickness = double.NaN }), "Invalid style rejected.");
Throws(() => document.Add(Item() with { Style = new(RibbonStyle: (RibbonStyle)99) }), "Unknown arrow silhouette rejected.");
Throws(() => document.Add(Item() with { Style = new(Align: (TextAlign)9) }), "Unknown text alignment rejected.");
Check(new AnnotationStyle().Bold && !new AnnotationStyle().Italic && new AnnotationStyle().Align == TextAlign.Left, "New text is bold and left aligned.");
Check(new AnnotationStyle().RibbonStyle == RibbonStyle.Wedge, "New arrows start with the wedge silhouette.");
Throws(() => document.Add(document.Annotations[0]), "Duplicate IDs rejected.");
document.Delete(document.Annotations[0].Id); document.Undo(); Check(document.Annotations.Count == 2, "Delete undo restores annotations.");
document.Replace(document.Annotations[0]); Check(document.CanRedo, "Equivalent replacement preserves redo.");
var sourcePosition = document.Annotations[0].Start;
document.CropTo(new(10, 20, 40, 30)); Check(document.Annotations[0].Start == sourcePosition, "Crop must not translate or destroy source annotations.");
document.ResetCrop(); Check(document.Annotations.Count == 2, "Crop reset preserves annotations.");
Throws(() => new EditableDocument(0, 8), "Zero image extent rejected.");
Throws(() => document.Add(Item() with { Start = new(double.PositiveInfinity, 0) }), "Invalid geometry rejected.");
Throws(() => document.Add(Item() with { Points = [new(double.NaN, 0)] }), "Invalid pen points rejected.");
Throws(() => document.Add(Item() with { Id = Guid.Empty }), "Empty identity rejected.");
var bounded = new EditableDocument(10, 10);
for (int i = 0; i < 140; i++) bounded.Add(Item());
int undoCount = 0; while (bounded.CanUndo) { bounded.Undo(); undoCount++; }
Check(undoCount == 128 && bounded.Annotations.Count == 12, "History is bounded.");
var transform = new ViewTransform(1.5, new(20, -8));
Check(transform.ToSource(transform.ToView(new(-13, 42))) == new PointD(-13, 42), "View/source round trip.");

byte[] Pixels(int w, int h) { var data = new byte[w * h * 4]; new Random(31).NextBytes(data); for (int i = 3; i < data.Length; i += 4) data[i] = 255; return data; }
foreach (int effect in new[] { 0, 1, 2 })
{
    var pixels = Pixels(10, 10); var before = pixels.ToArray();
    if (effect == 0) PixelEffects.Pixelate(pixels, 10, 10, new(2, 3, 4, 3), 3);
    if (effect == 1) PixelEffects.Blur(pixels, 10, 10, new(2, 3, 4, 3), 3);
    if (effect == 2) PixelEffects.Solid(pixels, 10, 10, new(2, 3, 4, 3), new(8, 7, 6));
    for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++)
        if (x < 2 || x >= 6 || y < 3 || y >= 6) Check(pixels.AsSpan((y * 10 + x) * 4, 4).SequenceEqual(before.AsSpan((y * 10 + x) * 4, 4)), "Effect leaked outside region.");
}
var clipped = Pixels(3, 3); PixelEffects.Solid(clipped, 3, 3, new(-10, -10, 11, 11), new(1, 2, 3, 4));
Check(clipped.AsSpan(0, 4).SequenceEqual(new byte[] { 3, 2, 1, 4 }), "Solid clips and writes BGRA correctly.");
var transparent = new byte[] { 0, 0, 0, 0, 0, 0, 255, 255 };
PixelEffects.Pixelate(transparent, 2, 1, new(0, 0, 2, 1), 2);
Check(transparent[2] == 255 && transparent[3] == 127, "Average alpha must not darken transparent edges.");
byte[] NaiveBox(byte[] source, int w, int h, int radius, bool horizontal)
{
    var result = new byte[source.Length];
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        long b = 0, g = 0, r = 0, a = 0; int count = 0;
        for (int d = -radius; d <= radius; d++)
        {
            int sx = x + (horizontal ? d : 0), sy = y + (horizontal ? 0 : d);
            if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
            int i = (sy * w + sx) * 4, alpha = source[i + 3];
            b += source[i] * alpha; g += source[i + 1] * alpha; r += source[i + 2] * alpha; a += alpha; count++;
        }
        int j = (y * w + x) * 4;
        result[j] = (byte)(a == 0 ? 0 : b / a); result[j + 1] = (byte)(a == 0 ? 0 : g / a); result[j + 2] = (byte)(a == 0 ? 0 : r / a); result[j + 3] = (byte)(a / count);
    }
    return result;
}
foreach (int w in new[] { 1, 2, 7 }) foreach (int h in new[] { 1, 3, 8 }) foreach (int radius in new[] { 1, 2, 10 })
{
    var source = new byte[w * h * 4]; new Random(w * 100 + h).NextBytes(source);
    var expected = NaiveBox(NaiveBox(source, w, h, radius, true), w, h, radius, false);
    var actual = source.ToArray(); PixelEffects.Blur(actual, w, h, new(0, 0, w, h), radius);
    Check(actual.SequenceEqual(expected), "Sliding blur must match independent box oracle, including edges and alpha.");
}
Throws(() => PixelEffects.Blur(new byte[7], 2, 1, new(0, 0, 2, 1)), "Reject undersized BGRA buffer.");
Throws(() => PixelEffects.Pixelate(new byte[8], 2, 1, new(0, 0, 2, 1), 0), "Reject zero block size.");
var outside = Pixels(2, 2); var outsideBefore = outside.ToArray();
PixelEffects.Blur(outside, 2, 2, new(-20, -20, 1, 1)); Check(outside.SequenceEqual(outsideBefore), "Outside effects are no-op.");

var flatGray = new byte[8 * 8 * 4]; for (int i = 0; i < flatGray.Length; i += 4) { flatGray[i] = flatGray[i + 1] = flatGray[i + 2] = 120; flatGray[i + 3] = 255; }
var seededA = flatGray.ToArray(); var seededB = flatGray.ToArray(); var seededAgain = flatGray.ToArray(); var plainBlocks = flatGray.ToArray();
PixelEffects.Pixelate(seededA, 8, 8, new(0, 0, 8, 8), 4, 11); PixelEffects.Pixelate(seededAgain, 8, 8, new(0, 0, 8, 8), 4, 11); PixelEffects.Pixelate(seededB, 8, 8, new(0, 0, 8, 8), 4, 97); PixelEffects.Pixelate(plainBlocks, 8, 8, new(0, 0, 8, 8), 4);
Check(plainBlocks.SequenceEqual(flatGray), "Pixelate without a seed stays exact.");
Check(seededA.SequenceEqual(seededAgain), "A seed gives the same blocks every time, so preview and export agree.");
Check(!seededA.SequenceEqual(seededB) && !seededA.SequenceEqual(flatGray), "Different seeds give different blocks.");
var seededMax = seededA.Where((_, i) => i % 4 != 3).Max(); var seededMin = seededA.Where((_, i) => i % 4 != 3).Min();
Check(seededMax <= 135 && seededMin >= 105, "The seeded offset stays small.");
Check(Enumerable.Range(0, 64).All(i => seededA[i * 4] == seededA[i * 4 + 1] && seededA[i * 4 + 1] == seededA[i * 4 + 2]), "The seeded offset keeps gray pixels gray.");
var arBounds = new RectD(0, 0, 100, 100);
Check(AspectMath.FromDrag(new(10, 10), new(50, 20), 2, arBounds) == new RectD(10, 10, 40, 20), "Dragging keeps the crop ratio.");
Check(AspectMath.FromDrag(new(50, 50), new(10, 40), 1, arBounds) == new RectD(10, 10, 40, 40), "Dragging up and left keeps the fixed corner.");
Check(AspectMath.FromDrag(new(90, 10), new(100, 100), 1, arBounds) == new RectD(90, 10, 10, 10), "The crop ratio holds at the bounds.");
Check(AspectMath.Resize(new(10, 10, 40, 20), new(10, 10, 60, 20), 2, arBounds) == new RectD(10, 10, 60, 30), "Widening also grows the height.");
Check(AspectMath.Resize(new(10, 10, 40, 20), new(10, 10, 40, 30), 2, arBounds) == new RectD(10, 10, 60, 30), "A height-only drag also grows the width.");
Check(AspectMath.Resize(new(40, 40, 40, 20), new(20, 40, 60, 20), 2, arBounds).Right == 80, "Dragging the left edge keeps the right edge.");
Check(new AnnotationStyle().Dim == .18, "A new spotlight dims lightly.");
var stamp = new DateTime(2026, 3, 4, 5, 6, 7, 8);
Check(FileNameTemplate.Render(FileNameTemplate.Default, stamp, "ab12") == "Snaplet-20260304-050607-008-ab12", "Default file name keeps its date, milliseconds and random part.");
Check(FileNameTemplate.Render("%Y/%m:%d", stamp, "x") == "2026_03_04", "Path separators and other invalid characters are replaced.");
Check(FileNameTemplate.Render("   ", stamp, "ab12").StartsWith("Snaplet-2026"), "A blank template falls back to the default.");
Check(FileNameTemplate.Render("...", stamp, "ab12").StartsWith("Snaplet-2026"), "A template with no usable characters falls back to the default.");
Check(FileNameTemplate.Render("100%% %q %", stamp, "x") == "100% %q %", "Literal percent signs and unknown tokens are kept.");
Check(FileNameTemplate.Render("con", stamp, "x") == "_con" && FileNameTemplate.Render("NUL.backup", stamp, "x") == "_NUL.backup", "Reserved device names cannot be produced.");
Check(FileNameTemplate.Render(new string('a', 300), stamp, "x").Length == 120, "Very long names are shortened.");
const int width = 32, height = 80, scroll = 17;
var full = Pixels(width, height + scroll);
var previous = full.AsSpan(0, width * height * 4).ToArray();
var current = full.AsSpan(width * scroll * 4, width * height * 4).ToArray();
var match = ScrollOverlapMatcher.Match(previous, current, width, height, 24);
Check(match.Accepted && match.Offset == scroll, "Distinct scroll overlap should match.");
Check(!ScrollOverlapMatcher.Match(previous, previous, width, height, 24).Accepted, "Stationary frame must not append.");
var repeated = new byte[width * height * 4];
for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { int i = (y * width + x) * 4; repeated[i] = repeated[i + 1] = repeated[i + 2] = (byte)(y % 8 < 4 ? 255 : 0); repeated[i + 3] = 255; }
Check(!ScrollOverlapMatcher.Match(repeated, repeated, width, height, 24).Accepted, "Repeated patterns must abstain.");
Check(!ScrollOverlapMatcher.Match(new byte[width * height * 4], new byte[width * height * 4], width, height, 24).Accepted, "Flat frame must abstain.");
var unrelated = Pixels(width, height); new Random(981).NextBytes(unrelated);
Check(!ScrollOverlapMatcher.Match(previous, unrelated, width, height, 24).Accepted, "Unrelated frames must abstain.");
var noisy = current.ToArray(); for (int i = 0; i < noisy.Length; i++) noisy[i] = (byte)Math.Clamp(noisy[i] + (i % 3) - 1, 0, 255);
Check(ScrollOverlapMatcher.Match(previous, noisy, width, height, 24).Accepted, "Small frame noise tolerable.");
Throws(() => ScrollOverlapMatcher.Match(previous, current, width, height, height), "Invalid overlap rejected.");
// A session must reconstruct every source row once across several different scroll distances.
var tallSource = Pixels(width, 160);
byte[] Tile(int y) => tallSource.AsSpan(y * width * 4, width * height * 4).ToArray();
var initialTile = Tile(0);
var stitch = new ScrollStitchSession(width, height, initialTile, minimumOverlap: 24);
initialTile[0] ^= 255;
Check(stitch.ToArray()[0] == tallSource[0], "Session owns initial frame.");
int totalOffset = 0;
foreach (int delta in new[] { 17, 13, 32 })
{
    totalOffset += delta;
    var tile = Tile(totalOffset);
    Check(stitch.Append(tile), "Distinct sequential tile accepted.");
    tile[0] ^= 255;
    Check(stitch.OutputHeight == height + totalOffset, "Stitch dimensions accumulate actual offsets.");
    Check(stitch.ToArray().SequenceEqual(tallSource.AsSpan(0, (height + totalOffset) * width * 4).ToArray()), "Stitch must reconstruct source exactly, without duplicated overlap.");
}
var stitchedSnapshot = stitch.ToArray(); stitchedSnapshot[0] ^= 255;
Check(stitch.ToArray()[0] == tallSource[0], "Output snapshots cannot mutate session.");
var beforeRejection = stitch.ToArray(); int beforeHeight = stitch.OutputHeight;
Check(!stitch.Append(unrelated) && stitch.LastRejection is not null, "Unrelated session frame rejected with reason.");
Check(stitch.OutputHeight == beforeHeight && stitch.ToArray().SequenceEqual(beforeRejection), "Rejected frame leaves output unchanged.");
Check(stitch.Append(Tile(totalOffset + 8)), "Rejected frame must not replace reference tile.");
Check(stitch.LastRejection is null, "Success clears rejection reason.");
var limited = new ScrollStitchSession(width, height, Tile(0), maxOutputBytes: width * (height + 10) * 4, minimumOverlap: 24);
Check(!limited.Append(Tile(17)) && limited.OutputHeight == height && limited.LastMatch.Accepted, "Byte cap rejects otherwise valid match.");
Check(limited.ToArray().SequenceEqual(Tile(0)), "Byte cap cannot partially mutate output.");
Check(limited.Append(Tile(8)), "Size-rejected tile must not replace reference tile.");
var heightLimited = new ScrollStitchSession(width, height, Tile(0), maxHeight: height + 10, minimumOverlap: 24);
Check(!heightLimited.Append(Tile(17)) && heightLimited.OutputHeight == height, "Height cap rejects before mutation.");
Check(!heightLimited.Append(Tile(0)), "Stationary session frame rejected.");
var flatSession = new ScrollStitchSession(width, height, new byte[width * height * 4], minimumOverlap: 24);
Check(!flatSession.Append(new byte[width * height * 4]) && flatSession.OutputHeight == height, "Flat session cannot duplicate rows.");
var noisySession = new ScrollStitchSession(width, height, previous, minimumOverlap: 24);
Check(noisySession.Append(noisy) && noisySession.OutputHeight == height + scroll, "Noisy session accepts confident overlap.");
Check(noisySession.ToArray().AsSpan(previous.Length).SequenceEqual(noisy.AsSpan((height - scroll) * width * 4)), "Appended rows come from latest tile including actual noise.");
Throws(() => stitch.Append(new byte[4]), "Session rejects changed frame dimensions.");
Throws(() => new ScrollStitchSession(width, height, Tile(0), maxHeight: height - 1, minimumOverlap: 24), "Session initial height respects cap.");
Throws(() => new ScrollStitchSession(width, height, Tile(0), maxOutputBytes: 4, minimumOverlap: 24), "Session initial bytes respect cap.");
// Editable callouts/curves and grouped property gestures retain their own history.
var curved=AnnotationGeometry.BendThrough(Item(),new(20,60));
Check(AnnotationGeometry.CurvePoint(curved,.5)==new PointD(20,60),"Bend handle lies on quadratic curve.");
Check(AnnotationGeometry.CurveDistance(curved,new(20,60))<.01,"Curved arrow can be hit at bent midpoint.");
Check(AnnotationGeometry.CurveDistance(curved,new(80,0))>20,"Curve hit test rejects distant pixels.");
var bubble=Item() with {Kind=AnnotationKind.Text,Text="한글",Control=new(4,7),Tail=new(12,90),Style=new(Bubble:true)};
var shifted=bubble.Move(10,-5);
Check(shifted.Control==new PointD(14,2) && shifted.Tail==new PointD(22,85),"Move preserves attached curve/tail geometry.");
Check(bubble.Tail==new PointD(12,90),"Move never mutates original callout.");
var grouped=new EditableDocument(400,300);grouped.Add(bubble);grouped.BeginEdit();
for(int i=5;i<=20;i++)grouped.Replace(bubble with {Thickness=i});
grouped.EndEdit();grouped.Undo();Check(grouped.Annotations.Single().Thickness==4,"Slider gesture undoes as one edit.");
grouped.Redo();Check(grouped.Annotations.Single().Thickness==20,"Slider gesture redoes final value.");
grouped.BeginEdit();grouped.Replace(bubble with {Thickness=9});grouped.CancelEdit();
Check(grouped.Annotations.Single().Thickness==20,"Cancelled style transaction restores starting object.");
grouped.Undo();Check(grouped.Annotations.Single().Thickness==4,"Cancelled edit does not consume prior history.");
var numbering=new EditableDocument(300,200);
var number=Item() with {Kind=AnnotationKind.Step,Text="1"};numbering.Add(number);
Check(numbering.NextCounter==2,"Counter increments after creation.");
numbering.Replace(number with {Text="7"});Check(numbering.NextCounter==8,"Editing number changes next counter.");
numbering.Undo();Check(numbering.NextCounter==2,"Undo restores next number along with object.");
numbering.Redo();Check(numbering.NextCounter==8,"Redo restores numbering sequence.");
numbering.Replace(numbering.Annotations.Single() with {Color=new(1,2,3)});
Check(numbering.NextCounter==8,"Color change cannot reset numbering sequence.");
numbering.Add(number with {Id=Guid.NewGuid(),Text="8"});Check(numbering.NextCounter==9,"Next counter follows custom numbering.");
numbering.Undo();Check(numbering.NextCounter==8,"Undo counter creation restores next number.");
var tip=AnnotationGeometry.CounterTip(number with {Start=new(100,100),Tail=new(1000,1000)});
Check(Math.Abs(AnnotationGeometry.Distance(new(100,100),tip)-AnnotationGeometry.CounterRadius(number)*1.55)<1e-8,"Counter pointer length is bounded.");
foreach(var body in new[]{new RectD(100,100,200,60),new RectD(100,100,20,20),new RectD(100,100,400,200)}) {
    foreach(var target in new[]{new PointD(-1000,-1000),new PointD(2000,2000),new PointD(2000,130),new PointD(120,-1000)}) {
        var boundedTip=AnnotationGeometry.CalloutTip(body,target);
        var edge=new PointD(Math.Clamp(boundedTip.X,body.X,body.Right),Math.Clamp(boundedTip.Y,body.Y,body.Bottom));
        Check(Math.Abs(AnnotationGeometry.Distance(edge,boundedTip)-Math.Clamp(Math.Min(body.Width,body.Height)*.6,24,48))<1e-8,"Callout reach stays bounded in axial and corner directions.");
        Check(AnnotationGeometry.CalloutTip(body,boundedTip)==boundedTip,"Bounded callout handle remains stable on repeated evaluation.");
    }
}
Throws(()=>grouped.Add(Item() with {Control=new(double.NaN,0)}),"Nonfinite curve rejected.");
Throws(()=>grouped.Add(Item() with {Tail=new(1,double.PositiveInfinity)}),"Nonfinite callout tail rejected.");
Throws(()=>grouped.Add(Item() with {Style=new(Opacity:double.NaN)}),"Nonfinite opacity rejected.");
Throws(()=>grouped.Add(Item() with {Style=new(Opacity:1.1)}),"Out of range opacity rejected.");
// Angle constraints preserve distance, including near-horizontal and vertical drags.
foreach(var p in new[]{new PointD(100,9),new PointD(8,100),new PointD(-75,80),new PointD(0,0)}) {
    var snapped=AnnotationGeometry.SnapAngle(new(0,0),p);
    Check(Math.Abs(AnnotationGeometry.Distance(new(0,0),p)-AnnotationGeometry.Distance(new(0,0),snapped))<1e-8,"Angle snap keeps drag length.");
}
var aligned=AnnotationGeometry.SnapAxis(31,71,new[]{10d,30d,90d},3);
Check(aligned.Offset==-1 && aligned.Guide==30,"Closest edge aligns within visible tolerance.");
var unaligned=AnnotationGeometry.SnapAxis(31,71,new[]{10d,90d},3);
Check(unaligned.Offset==0 && unaligned.Guide==null,"Distant object cannot attract drag.");
var decorated=new EditableDocument(400,300);decorated.Add(bubble with {Style=new(Fill:ShapeFill.Hatched,Sketch:true)});
decorated.Replace(decorated.Annotations[0] with {Style=new(Fill:ShapeFill.Solid)});decorated.Undo();
Check(decorated.Annotations[0].Appearance is {Sketch:true,Fill:ShapeFill.Hatched},"Undo preserves organic shape and hatch styles.");
var shapeTailDoc=new EditableDocument(400,300);
var shapeCallout=Item() with {Kind=AnnotationKind.Rectangle,Tail=new(80,180),Style=new(ShapeTail:true)};
shapeTailDoc.Add(shapeCallout);shapeTailDoc.Replace(shapeCallout.Move(20,-10));shapeTailDoc.Undo();
Check(shapeTailDoc.Annotations[0].Tail==new PointD(80,180) && AnnotationGeometry.HasTail(shapeTailDoc.Annotations[0]),"Undo shape movement restores attached tail and option.");
Check(!AnnotationGeometry.HasTail(shapeCallout with {Style=new()}),"Shape tails are opt-in even though counter tail defaults on.");
Check(AnnotationGeometry.HasTail(shapeCallout with {Kind=AnnotationKind.Ellipse}),"Ellipse supports the same explicit tail option.");
Check(!AnnotationGeometry.HasTail(shapeCallout with {Kind=AnnotationKind.Arrow}),"Shape tail option does not affect unrelated tools.");
AdvancedModelChecks.Run(Check);
PrecisionChecks.Run(Check);
BackdropChecks.Run(Check);
ColorChecks.Run(Check);
ScrollBandChecks.Run(Check);
ProjectFileChecks.Run(Check);
RecordingChecks.Run(Check);
byte[] twoByThree = Enumerable.Range(0, 24).Select(i => (byte)i).ToArray();
var turned = ScrollStitchSession.Transpose(twoByThree, 2, 3);
Check(turned.AsSpan(4, 4).SequenceEqual(twoByThree.AsSpan(8, 4)) && turned.AsSpan(12, 4).SequenceEqual(twoByThree.AsSpan(4, 4)), "Transposing swaps rows and columns.");
Check(ScrollStitchSession.Transpose(turned, 3, 2).SequenceEqual(twoByThree), "Transposing twice restores the image.");
Throws(() => ScrollStitchSession.Transpose(twoByThree, 3, 3), "Transposing checks the image size.");
var wideRandom = new Random(5); var widePage = new byte[400 * 60 * 4]; wideRandom.NextBytes(widePage); for (int i = 3; i < widePage.Length; i += 4) widePage[i] = 255;
byte[] WideView(int left) { var view = new byte[120 * 60 * 4]; for (int y = 0; y < 60; y++) widePage.AsSpan((y * 400 + left) * 4, 120 * 4).CopyTo(view.AsSpan(y * 120 * 4)); return ScrollStitchSession.Transpose(view, 120, 60); }
var wideSession = new ScrollStitchSession(60, 120, WideView(0), minimumOverlap: 30);
Check(wideSession.Append(WideView(50)) && wideSession.Append(WideView(130)) && wideSession.Append(WideView(210)) && wideSession.Append(WideView(280)), "Sideways frames stitch through the same session.");
Check(wideSession.OutputHeight == 400 && ScrollStitchSession.Transpose(wideSession.ToArray(), 60, 400).SequenceEqual(widePage), "A sideways stitch transposes back to the original page.");
var capturedAt = new DateTime(2026, 11, 8, 3, 4, 5, 678, DateTimeKind.Utc);
Check(HistoryNames.TryParseUtc(HistoryNames.Create(capturedAt, "abc123"), out var parsedAt) && parsedAt == capturedAt && parsedAt.Kind == DateTimeKind.Utc, "A history name gives back its capture time.");
Check(!HistoryNames.TryParseUtc("photo.png", out _) && !HistoryNames.TryParseUtc("", out _), "Other file names are not history entries.");
Check(string.CompareOrdinal(HistoryNames.Create(capturedAt, "zzzzzz"), HistoryNames.Create(capturedAt.AddMilliseconds(1), "aaaaaa")) < 0, "History names sort by time, not by their unique part.");
var galleryNow = new DateTime(2026, 11, 8, 0, 30, 0);
Check(HistoryNames.Heading(galleryNow.AddMinutes(-10), galleryNow) == "오늘", "A capture from the same day is today.");
Check(HistoryNames.Heading(galleryNow.AddHours(-1), galleryNow) == "어제", "An hour ago can already be yesterday.");
Check(HistoryNames.Heading(galleryNow.AddDays(-3), galleryNow) == "11월 5일", "Older captures show their date.");
Check(HistoryNames.Heading(galleryNow.AddYears(-1), galleryNow) == "2025년 11월 8일", "Other years show the year.");
Console.WriteLine($"PASS: {passed} assertions (geometry, history, pixel effects, scroll matching).");
