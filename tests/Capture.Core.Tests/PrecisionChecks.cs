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
