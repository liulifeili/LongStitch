using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;

namespace LongStitch.Core.Matching;

/// <summary>Find vertical offsets supported by identical, non-uniform screenshot rows.</summary>
internal static class ExactRowMatcher
{
    internal readonly record struct Evidence(int Overlap, int Rows, int RunnerUpRows, int ContiguousRows = 0);

    internal static Evidence? Find(RgbaImage before, RgbaImage after,
        IntRange beforeRows, IntRange afterRows, IntRange columns, int minimumOverlap)
    {
        var first = Index(before, beforeRows, columns);
        var second = Index(after, afterRows, columns);
        var votes = new Dictionary<int, int>();
        foreach (var (hash, aRows) in first)
        {
            if (aRows.Count > 3 || !second.TryGetValue(hash, out var bRows) || bRows.Count > 3)
                continue;
            foreach (int a in aRows)
            foreach (int b in bRows)
            {
                int offset = a - b;
                int overlap = before.Height - offset;
                if (offset <= 0 || overlap < minimumOverlap || overlap > Math.Min(before.Height, after.Height))
                    continue;
                votes.TryGetValue(overlap, out int count);
                votes[overlap] = count + 1;
            }
        }

        var best = votes.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(8).ToArray();
        // Fewer than 24 independent rows is usually one repeated avatar/icon or a
        // nearly blank message area. It may suggest a location, but cannot support
        // an automatic pass.
        if (best.Length == 0 || best[0].Value < 24) return null;
        int runnerUp = best.Length > 1 ? best[1].Value : 0;
        // Repeated avatars, icons and list rows can match at several unrelated offsets.
        if (runnerUp * 2 >= best[0].Value) return null;

        // A collection of identical divider/background rows is not evidence of a
        // scroll overlap. Require one continuous run of exact rows as well; this
        // is the guard that prevents unrelated screenshots with a common grey
        // separator from being forced together.
        foreach (var candidate in best)
        {
            int overlap = candidate.Key;
            int baseRow = before.Height - overlap;
            int run = 0, longest = 0, rows = 0;
            int lo = Math.Max(0, Math.Max(beforeRows.Start, baseRow));
            int hi = Math.Min(before.Height, Math.Min(beforeRows.End, baseRow + after.Height));
            for (int a = lo; a < hi; a++)
            {
                int b = a - baseRow;
                bool equal = RowEqual(before, after, a, b, columns);
                if (equal) { rows++; run++; if (run > longest) longest = run; }
                else run = 0;
            }
            int requiredRun = Math.Min(96, Math.Max(12, Math.Min(overlap / 5, 32)));
            if (rows >= 24 && longest >= requiredRun)
                return new Evidence(overlap, rows, runnerUp, longest);
        }
        return null;
    }

    private static Dictionary<ulong, List<int>> Index(RgbaImage image, IntRange rows, IntRange columns)
    {
        var index = new Dictionary<ulong, List<int>>();
        int start = Math.Clamp(columns.Start + columns.Length * 15 / 100, 0, image.Width);
        int end = Math.Clamp(columns.Start + columns.Length * 90 / 100, start, image.Width);
        for (int y = Math.Max(0, rows.Start); y < Math.Min(image.Height, rows.End); y++)
        {
            ReadOnlySpan<byte> row = image.Row(y);
            ulong hash = 14695981039346656037UL;
            long sum = 0, squared = 0;
            int count = 0;
            for (int x = start; x < end; x += 2)
            for (int channel = 0; channel < 3; channel++)
            {
                byte value = row[x * 4 + channel];
                hash = unchecked((hash ^ value) * 1099511628211UL);
                sum += value;
                squared += value * value;
                count++;
            }
            if (count == 0 || squared * count - sum * sum < 64L * count * count) continue;
            if (!index.TryGetValue(hash, out var found)) index[hash] = found = new List<int>(1);
            // Four occurrences are enough to mark this row as repetitive.
            if (found.Count < 4) found.Add(y);
        }
        return index;
    }

    private static bool RowEqual(RgbaImage a, RgbaImage b, int ay, int by, IntRange columns)
    {
        if (ay < 0 || by < 0 || ay >= a.Height || by >= b.Height) return false;
        ReadOnlySpan<byte> ra = a.Row(ay), rb = b.Row(by);
        int start = Math.Clamp(columns.Start, 0, Math.Min(a.Width, b.Width));
        int end = Math.Clamp(columns.End, start, Math.Min(a.Width, b.Width));
        // Sample every second pixel for speed, but include both ends. This still
        // rejects flat-row coincidences while tolerating no visual resampling.
        for (int x = start; x < end; x += 2)
        {
            int i = x * 4;
            if (ra[i] != rb[i] || ra[i + 1] != rb[i + 1] || ra[i + 2] != rb[i + 2]) return false;
        }
        return true;
    }
}
