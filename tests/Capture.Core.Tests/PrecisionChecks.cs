using Capture.Core;

/// <summary>Selection arithmetic and pixel probes used by the capture overlay and editor precision tools.</summary>
internal static class PrecisionChecks
{
    private static byte[] Canvas(int width, int height, (byte R, byte G, byte B) fill)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = fill.B; px[i + 1] = fill.G; px[i + 2] = fill.R; px[i + 3] = 255; }
        return px;
    }
    private static void Paint(byte[] px, int width, PixelRect r, (byte R, byte G, byte B) c)
    {
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++) { int i = (y * width + x) * 4; px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
    }

    internal static void Run(Action<bool, string> check)
    {
        void Rejects(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            check(rejected, message);
        }
        var bounds = new PixelRect(0, 0, 200, 100);

        // Dragging in any direction normalises; square follows the longer side and stays inside the monitor.
        check(SelectionMath.FromDrag(50, 40, 10, 20, bounds) == new PixelRect(10, 20, 40, 20), "Reverse drag normalises to a positive rectangle.");
        check(SelectionMath.FromDrag(50, 40, 500, -30, bounds) == new PixelRect(50, 0, 150, 40), "Drag clamps to the monitor bounds.");
        check(SelectionMath.FromDrag(10, 10, 40, 20, bounds, square: true) == new PixelRect(10, 10, 30, 30), "Shift square uses the longer side.");
        check(SelectionMath.FromDrag(190, 50, 100, 100, bounds, square: true) == new PixelRect(140, 50, 50, 50), "Square never leaves bounds even when the longer side would.");
        check(SelectionMath.FromDrag(30, 30, 30, 30, bounds).IsEmpty, "A click without movement yields an empty selection.");

        // Keyboard nudge and resize keep the selection whole and inside the monitor.
        var sel = new PixelRect(10, 10, 50, 40);
        check(SelectionMath.Move(sel, 1, 0, bounds) == sel with { X = 11 }, "Arrow key moves by one pixel.");
        check(SelectionMath.Move(sel, -100, 500, bounds) == new PixelRect(0, 60, 50, 40), "Move clamps without shrinking.");
        check(SelectionMath.Move(new PixelRect(0, 0, 400, 400), 3, 3, bounds) == bounds, "Oversized selection is limited to bounds.");
        check(SelectionMath.ResizeBy(sel, 10, -100, bounds) == new PixelRect(10, 10, 60, 1), "Resize keeps at least one pixel.");
        check(SelectionMath.ResizeBy(sel, 1000, 0, bounds).Right == 200, "Resize stops at the monitor edge.");
        check(SelectionMath.Grow(sel, 1, bounds) == new PixelRect(9, 9, 52, 42), "Bracket grows every side by one pixel.");
        check(SelectionMath.Grow(new PixelRect(0, 0, 10, 10), 10, bounds) == new PixelRect(0, 0, 20, 20), "Grow clips at the monitor origin.");
        check(SelectionMath.Grow(new PixelRect(10, 10, 2, 2), -5, bounds) is { Width: 1, Height: 1 }, "Shrink cannot collapse below one pixel.");

        // Handles: each moves only its own edges and crossing flips cleanly.
        check(SelectionMath.Resize(sel, SelectionHandle.Right, 100, 999, bounds) == new PixelRect(10, 10, 90, 40), "Edge handle ignores the other axis.");
        check(SelectionMath.Resize(sel, SelectionHandle.TopLeft, 0, 0, bounds) == new PixelRect(0, 0, 60, 50), "Corner handle moves two edges.");
        check(SelectionMath.Resize(sel, SelectionHandle.Left, 100, 0, bounds) == new PixelRect(60, 10, 40, 40), "Crossing the opposite edge flips.");
        check(SelectionMath.Resize(sel, SelectionHandle.Bottom, 0, 10, bounds).Height == 1, "Collapsed handle keeps one pixel.");

        // Pixel probes: colour, hex, average and line runs.
        var px = Canvas(8, 4, (255, 255, 255));
        Paint(px, 8, new PixelRect(2, 1, 3, 2), (18, 52, 86));
        check(PixelProbe.At(px, 8, 3, 1) == new ColorRgba(18, 52, 86), "Pixel probe reads BGRA as RGB.");
        check(PixelProbe.Hex(PixelProbe.At(px, 8, 3, 1)) == "#123456", "Hex uses upper-case six digits.");
        check(PixelProbe.Rgb(new ColorRgba(1, 2, 3)) == "rgb(1, 2, 3)", "rgb() formatting.");
        Rejects(() => PixelProbe.At(px, 8, 8, 0), "Probe rejects x beyond the row.");
        Rejects(() => PixelProbe.At(px, 8, 0, 4), "Probe rejects y beyond the buffer.");
        check(PixelProbe.Average(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }) == new ColorRgba(128, 128, 128), "Average rounds to nearest.");
        Rejects(() => PixelProbe.Average(new byte[3]), "Average rejects partial pixels.");
        var row = px.AsSpan((1 * 8) * 4, 8 * 4);
        check(PixelProbe.Run(row, 3, 0) == (1, 1), "Run measures the coloured span around the pointer.");
        check(PixelProbe.Run(row, 0, 0) == (0, 1), "Run measures the background up to the first change.");
        check(PixelProbe.Run(row, 0, 255) == (0, 7), "Unlimited tolerance spans the whole line.");

        // Smart trim: independent margins of different colours trim to the content box.
        var page = Canvas(40, 30, (240, 240, 240));
        Paint(page, 40, new PixelRect(0, 0, 40, 4), (30, 30, 30));      // dark title bar above
        Paint(page, 40, new PixelRect(12, 10, 9, 7), (200, 0, 0));       // content
        Paint(page, 40, new PixelRect(15, 12, 2, 2), (0, 0, 200));
        var first = PixelProbe.TrimUniformBorder(page, 40, 30, new PixelRect(0, 0, 40, 30), 8);
        check(first == new PixelRect(12, 4, 9, 13), "Each side trims its own margin colour; a different band (title bar to page) stops that side.");
        check(PixelProbe.TrimUniformBorder(page, 40, 30, new PixelRect(0, 4, 40, 26), 8) == new PixelRect(12, 10, 9, 7), "Below the title bar the light page margin trims to content.");
        check(PixelProbe.TrimUniformBorder(page, 40, 30, new PixelRect(5, 6, 30, 20), 8) == new PixelRect(12, 10, 9, 7), "Trim works inside a sub-selection.");
        check(PixelProbe.TrimUniformBorder(page, 40, 30, new PixelRect(25, 20, 10, 5), 8) == new PixelRect(25, 20, 10, 5), "A uniform area keeps the original selection.");
        check(PixelProbe.TrimUniformBorder(page, 40, 30, new PixelRect(30, 25, 50, 50), 8) == new PixelRect(30, 25, 10, 5), "Trim clips a selection extending outside the image.");
        var noisy = Canvas(10, 10, (100, 100, 100));
        Paint(noisy, 10, new PixelRect(0, 0, 10, 1), (104, 98, 101));
        Paint(noisy, 10, new PixelRect(4, 4, 2, 2), (0, 0, 0));
        check(PixelProbe.TrimUniformBorder(noisy, 10, 10, new PixelRect(0, 0, 10, 10), 6) == new PixelRect(4, 4, 2, 2), "Tolerance absorbs compression noise in margins.");
        var divider = Canvas(20, 5, (255, 255, 255));
        Paint(divider, 20, new PixelRect(3, 2, 10, 1), (0, 0, 0));
        check(PixelProbe.TrimUniformBorder(divider, 20, 5, new PixelRect(0, 0, 20, 5), 8) == new PixelRect(3, 2, 10, 1), "A one-pixel divider keeps its full length.");
        Rejects(() => PixelProbe.TrimUniformBorder(new byte[8], 4, 4, new PixelRect(0, 0, 4, 4), 0), "Trim rejects a buffer smaller than its dimensions.");
    }
}

/// <summary>Backdrop is document state: undoable, validated and independent of crop and annotations.</summary>
internal static class BackdropChecks
{
    internal static void Run(Action<bool, string> check)
    {
        void Rejects(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            check(rejected, message);
        }
        var doc = new EditableDocument(100, 60);
        check(!doc.Backdrop.Enabled && doc.Backdrop.PaddingPixels == 0, "New documents export without a backdrop.");
        doc.SetBackdrop(new Backdrop(Enabled: true, Padding: 40.4));
        check(doc.Backdrop.PaddingPixels == 40 && doc.CanUndo, "Enabling the backdrop is an undoable step with whole-pixel padding.");
        doc.CropTo(new RectD(10, 10, 50, 30));
        check(doc.Backdrop.Enabled, "Crop keeps the backdrop.");
        doc.Undo(); check(doc.Backdrop.Enabled && doc.Crop.Width == 100, "Undo of crop leaves the backdrop.");
        doc.Undo(); check(!doc.Backdrop.Enabled, "Undo removes the backdrop.");
        doc.Redo(); check(doc.Backdrop.Padding == 40.4, "Redo restores the exact backdrop.");
        int before = doc.CanUndo ? 1 : 0;
        doc.SetBackdrop(doc.Backdrop); check(doc.CanRedo, "Setting an identical backdrop is a no-op that keeps redo.");
        doc.BeginEdit();
        for (int i = 0; i <= 10; i++) doc.SetBackdrop(doc.Backdrop with { Shadow = i / 10.0 });
        doc.EndEdit(); doc.Undo(); check(doc.Backdrop.Shadow == .5, "A slider drag inside one edit undoes in a single step.");
        Rejects(() => doc.SetBackdrop(new Backdrop(Padding: -1)), "Negative padding rejected.");
        Rejects(() => doc.SetBackdrop(new Backdrop(Padding: double.NaN)), "Nonfinite padding rejected.");
        Rejects(() => doc.SetBackdrop(new Backdrop(Preset: Backdrop.PresetCount)), "Unknown gradient preset rejected.");
        Rejects(() => doc.SetBackdrop(new Backdrop(Shadow: 1.5)), "Shadow above one rejected.");
        Rejects(() => doc.SetBackdrop(new Backdrop(Fill: (BackdropFill)9)), "Unknown fill rejected.");
        check(before >= 0, "Backdrop checks completed.");
    }
}

/// <summary>Colour formats and contrast against published reference values.</summary>
internal static class ColorChecks
{
    internal static void Run(Action<bool, string> check)
    {
        ColorRgba black = new(0, 0, 0), white = new(255, 255, 255), red = new(255, 0, 0);
        check(ColorMath.Hsl(red) == "hsl(0, 100%, 50%)" && ColorMath.Hsl(new ColorRgba(0, 128, 128)) == "hsl(180, 100%, 25%)", "HSL matches CSS for red and teal.");
        check(ColorMath.Hsl(new ColorRgba(128, 128, 128)) == "hsl(0, 0%, 50%)", "Grey has zero saturation.");
        var (l, c, h) = ColorMath.Oklch(red);
        check(Math.Abs(l - 0.628) < 0.002 && Math.Abs(c - 0.2577) < 0.002 && Math.Abs(h - 29.23) < 0.3, "OKLCH of sRGB red matches the CSS reference.");
        check(ColorMath.OklchText(white).StartsWith("oklch(100% 0 "), "White is 100% lightness with no chroma.");
        check(Math.Abs(ColorMath.WcagRatio(black, white) - 21) < 1e-9 && ColorMath.WcagRatio(white, white) == 1, "WCAG ratio spans 1 to 21.");
        check(Math.Abs(ColorMath.WcagRatio(new ColorRgba(0x77, 0x77, 0x77), white) - 4.48) < 0.01, "#777 on white is just below AA (4.48).");
        check(ColorMath.WcagLevel(4.48) == "AA 큰 글자" && ColorMath.WcagLevel(4.5) == "AA" && ColorMath.WcagLevel(7.1) == "AAA" && ColorMath.WcagLevel(2) == "미달", "WCAG levels use 3, 4.5 and 7 thresholds.");
        check(Math.Abs(ColorMath.ApcaLc(black, white) - 106.04) < 0.05, "APCA black on white is Lc 106.04.");
        check(Math.Abs(ColorMath.ApcaLc(white, black) + 107.88) < 0.05, "APCA white on black is Lc -107.88.");
        check(ColorMath.ApcaLc(new ColorRgba(120, 120, 120), new ColorRgba(121, 121, 121)) == 0, "Near-identical colours have no APCA contrast.");
    }
}

/// <summary>Sticky headers/footers and end-of-page detection for automatic scrolling.</summary>
internal static class ScrollBandChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const int width = 40, frameHeight = 100, header = 8, footer = 10;
        var random = new Random(4242);
        var page = new byte[width * 400 * 4]; random.NextBytes(page);
        for (int i = 3; i < page.Length; i += 4) page[i] = 255;
        var headerRows = new byte[width * header * 4]; random.NextBytes(headerRows);
        var footerRows = new byte[width * footer * 4]; random.NextBytes(footerRows);
        for (int i = 3; i < headerRows.Length; i += 4) headerRows[i] = 255;
        for (int i = 3; i < footerRows.Length; i += 4) footerRows[i] = 255;
        int row = width * 4;
        byte[] Frame(int scroll)
        {
            var f = new byte[width * frameHeight * 4];
            headerRows.CopyTo(f, 0);
            page.AsSpan((scroll + header) * row, (frameHeight - header - footer) * row).CopyTo(f.AsSpan(header * row));
            footerRows.CopyTo(f, (frameHeight - footer) * row);
            return f;
        }
        var bands = ScrollStitchSession.StaticBands(Frame(0), Frame(30), width, frameHeight);
        check(bands == (header, footer), $"Static band detection finds the sticky header and footer (got {bands}).");
        check(ScrollStitchSession.StaticBands(Frame(5), Frame(5), width, frameHeight) == (frameHeight, 0), "Identical frames are fully static.");
        var session = new ScrollStitchSession(width, frameHeight, Frame(0), minimumOverlap: 16);
        check(session.Append(Frame(30)) && session.Append(Frame(60)), "Frames with sticky bars still match on the moving middle.");
        check(session.OutputHeight == frameHeight + 60 && session.LastStaticBands == (header, footer), "Height grows by the content scroll only.");
        var expected = new byte[width * (frameHeight + 60) * 4];
        headerRows.CopyTo(expected, 0);
        page.AsSpan(header * row, (frameHeight + 60 - header - footer) * row).CopyTo(expected.AsSpan(header * row));
        footerRows.CopyTo(expected, (frameHeight + 60 - footer) * row);
        check(session.ToArray().SequenceEqual(expected), "Output has the header once, continuous content, and the footer once at the end.");
        check(!session.Append(Frame(60)) && session.ReachedEnd && session.OutputHeight == frameHeight + 60, "An unchanged frame signals the end without appending.");
        check(session.Append(Frame(75)) && !session.ReachedEnd, "Scrolling again after an unchanged frame continues the stitch.");
    }
}
