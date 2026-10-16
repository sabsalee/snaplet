namespace Capture.Core;

public readonly record struct ScrollMatch(bool Accepted, int Offset, double Confidence, double MeanError);

/// <summary>Conservative downward-scroll overlap detector. Ambiguous/flat/changed frames abstain instead of corrupting a stitch.</summary>
public static class ScrollOverlapMatcher
{
    public static ScrollMatch Match(ReadOnlySpan<byte> previousBgra, ReadOnlySpan<byte> currentBgra, int width, int height, int minimumOverlap = 32)
    {
        long size = (long)width * height * 4;
        if (width <= 0 || height <= 0 || size > int.MaxValue || previousBgra.Length != size || currentBgra.Length != size) throw new ArgumentException("Expected equally sized BGRA32 frames.");
        if (minimumOverlap < 4 || minimumOverlap >= height) throw new ArgumentOutOfRangeException(nameof(minimumOverlap));
        int stepX = Math.Max(1, width / 64), stepY = Math.Max(1, height / 128);
        double sum = 0, sumSquared = 0; int sampleCount = 0;
        for (int y = 0; y < height; y += stepY) for (int x = 0; x < width; x += stepX)
        {
            int i = (y * width + x) * 4;
            double v = (currentBgra[i] + currentBgra[i + 1] + currentBgra[i + 2]) / 765.0;
            sum += v; sumSquared += v * v; sampleCount++;
        }
        if (sumSquared / sampleCount - Math.Pow(sum / sampleCount, 2) < .004) return new(false, 0, 0, 1);
        var errors = new double[height - minimumOverlap + 1];
        int best = 0; double bestError = double.PositiveInfinity;
        for (int offset = 0; offset < errors.Length; offset++)
        {
            long error = 0; int count = 0;
            for (int y = 0; y < height - offset; y += stepY) for (int x = 0; x < width; x += stepX)
            {
                int a = ((y + offset) * width + x) * 4, b = (y * width + x) * 4;
                error += Math.Abs(previousBgra[a] - currentBgra[b]) + Math.Abs(previousBgra[a + 1] - currentBgra[b + 1]) + Math.Abs(previousBgra[a + 2] - currentBgra[b + 2]); count++;
            }
            errors[offset] = error / (count * 765.0);
            if (errors[offset] < bestError) { bestError = errors[offset]; best = offset; }
        }
        double runnerUp = double.PositiveInfinity;
        for (int i = 0; i < errors.Length; i++) if (Math.Abs(i - best) > 2) runnerUp = Math.Min(runnerUp, errors[i]);
        if (!double.IsFinite(runnerUp)) return new(false, best, 0, bestError);
        double gap = runnerUp - bestError;
        double confidence = Math.Clamp(gap / .08, 0, 1) * Math.Clamp(1 - bestError / .06, 0, 1);
        return new(best > 0 && bestError < .06 && gap > .01 && confidence >= .25, best, confidence, bestError);
    }
}
