namespace MeetingSummaries;

/// <summary>A port of Python's difflib.SequenceMatcher(None, a, b, autojunk=False).get_matching_blocks(), for lists of
/// words: the same algorithm, so the same blocks, ties included. (No junk and no "popular" elements, as with
/// autojunk=False.)</summary>
public static class SequenceMatcher
{
    public readonly record struct Match(int A, int B, int Size);

    public static List<Match> MatchingBlocks(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var b2j = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var j = 0; j < b.Count; j++)
        {
            if (!b2j.TryGetValue(b[j], out var indices))
                b2j[b[j]] = indices = [];
            indices.Add(j);
        }
        var queue = new List<(int ALo, int AHi, int BLo, int BHi)> { (0, a.Count, 0, b.Count) };
        var blocks = new List<Match>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue[^1];
            queue.RemoveAt(queue.Count - 1);
            var x = LongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (x.Size > 0)
            {
                blocks.Add(x);
                if (alo < x.A && blo < x.B)
                    queue.Add((alo, x.A, blo, x.B));
                if (x.A + x.Size < ahi && x.B + x.Size < bhi)
                    queue.Add((x.A + x.Size, ahi, x.B + x.Size, bhi));
            }
        }
        blocks.Sort((p, q) => p.A != q.A ? p.A.CompareTo(q.A) : p.B != q.B ? p.B.CompareTo(q.B) : p.Size.CompareTo(q.Size));
        int i1 = 0, j1 = 0, k1 = 0;
        var result = new List<Match>();
        foreach (var (i2, j2, k2) in blocks)
        {
            if (i1 + k1 == i2 && j1 + k1 == j2)
                k1 += k2;
            else
            {
                if (k1 > 0)
                    result.Add(new Match(i1, j1, k1));
                (i1, j1, k1) = (i2, j2, k2);
            }
        }
        if (k1 > 0)
            result.Add(new Match(i1, j1, k1));
        result.Add(new Match(a.Count, b.Count, 0));
        return result;
    }

    private static Match LongestMatch(IReadOnlyList<string> a, IReadOnlyList<string> b, Dictionary<string, List<int>> b2j,
        int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var js))
            {
                foreach (var j in js)
                {
                    if (j < blo)
                        continue;
                    if (j >= bhi)
                        break;
                    var k = newj2len[j] = j2len.GetValueOrDefault(j - 1) + 1;
                    if (k > bestsize)
                        (besti, bestj, bestsize) = (i - k + 1, j - k + 1, k);
                }
            }
            j2len = newj2len;
        }
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
            (besti, bestj, bestsize) = (besti - 1, bestj - 1, bestsize + 1);
        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
            bestsize++;
        return new Match(besti, bestj, bestsize);
    }
}
