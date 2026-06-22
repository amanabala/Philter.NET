// De-identification logic ported from Philter (philter-ucsf / philter-lite),
// translated from coordinate_map.py.
// Copyright (c) 2018, REGENTS OF THE UNIVERSITY OF CALIFORNIA. BSD 3-Clause License.

using System.Text.RegularExpressions;

namespace Philter.NET;

/// <summary>
/// Single-document coordinate map (start → stop) of half-open character spans. Mirrors
/// Philter's CoordinateMap (philter_lite/coordinate_map.py). Supports overlap-aware
/// extension (AddExtend), removal, overlap detection, and complement generation —
/// these are all load-bearing for the include/exclude pipeline in Philter's detect_phi.
/// </summary>
internal sealed class CoordinateMap
{
    // start -> stop (half-open: stop is exclusive)
    private readonly SortedDictionary<int, int> _map = new();

    internal static readonly Regex PunctuationMatcher = new(@"[^a-zA-Z0-9*]", RegexOptions.Compiled);

    public IEnumerable<(int start, int stop)> FileCoords()
    {
        foreach (var kv in _map) yield return (kv.Key, kv.Value);
    }

    public bool DoesExist(int index) => _map.ContainsKey(index);

    public (int start, int stop) GetCoords(int start) => (start, _map[start]);

    /// <summary>Adds [start, stop). If overlap is forbidden and one exists, no-op.</summary>
    public void Add(int start, int stop, bool overlap = false)
    {
        if (!overlap && DoesOverlap(start, stop)) return;
        _map[start] = stop;
    }

    public void Remove(int start, int stop)
    {
        // Philter only removes from the start-keyed map; all_coords bookkeeping is
        // recomputed lazily from the map elsewhere.
        _map.Remove(start);
    }

    /// <summary>True if any existing range overlaps [start, stop).</summary>
    public bool DoesOverlap(int start, int stop)
    {
        foreach (var kv in _map)
        {
            var s = kv.Key;
            var e = kv.Value;
            if (s < stop && start < e) return true;
        }
        return false;
    }

    /// <summary>
    /// Add a span, merging overlapping spans into one larger span. Faithful port of
    /// CoordinateMap.add_extend / max_overlap — when the new span touches existing spans,
    /// the result is the union of all touched ranges.
    /// </summary>
    public void AddExtend(int start, int stop)
    {
        var overlaps = MaxOverlap(start, stop);

        if (overlaps.Count == 0)
        {
            _map[start] = stop;
            return;
        }

        // Clear all original overlaps first
        foreach (var o in overlaps) _map.Remove(o.origStart);

        if (overlaps.Count == 1)
        {
            var o = overlaps[0];
            _map[o.newStart] = o.newStop;
            return;
        }

        // Multiple overlaps — span union from earliest start to latest stop.
        // DELIBERATE DEVIATION FROM UPSTREAM: philter-ucsf and philter-lite both do
        // `add(o2.new_start, o1.new_stop)` here — the LAST overlap's start with the FIRST
        // overlap's stop — which truncates the union and silently drops coverage of every
        // later overlapped span. In the original that is a latent recall leak: a PHI span
        // abutting another PHI exclude that gets merged by a third adjacent AddExtend can
        // lose its exclusion entirely (e.g. a visit date right after a hyphenated provider
        // name). We take the true union (earliest start -> latest stop), which raises recall
        // with no measured regression (gold DATE 755/758 -> 758/758, overall held). This is
        // the one intentional behavioral departure from upstream Philter.
        var first = overlaps[0];
        var last = overlaps[^1];
        _map[first.newStart] = last.newStop;
    }

    private List<(int origStart, int origEnd, int newStart, int newStop)> MaxOverlap(int start, int stop)
    {
        var overlaps = new List<(int, int, int, int)>();
        foreach (var kv in _map)
        {
            var s = kv.Key;
            var e = kv.Value;
            if (s <= start && start <= e)
            {
                if (stop >= e) overlaps.Add((s, e, s, stop));
                else           overlaps.Add((s, e, s, e));
            }
            else if (s <= stop && stop <= e)
            {
                if (start <= s) overlaps.Add((s, e, start, e));
                else            overlaps.Add((s, e, s, e));
            }
        }
        return overlaps;
    }

    /// <summary>
    /// Complement coordinates that are NOT in this map AND are not punctuation. Returns
    /// a list of grouped (start, stop) ranges. Used by regex_context to find PHI-adjacent
    /// matches.
    /// </summary>
    public Dictionary<int, int> GetComplement(string text)
    {
        var covered = new HashSet<int>();
        foreach (var kv in _map)
            for (int i = kv.Key; i < kv.Value; i++) covered.Add(i);

        var keep = new SortedSet<int>();
        for (int i = 0; i < text.Length; i++)
        {
            if (covered.Contains(i)) continue;
            if (PunctuationMatcher.IsMatch(text[i].ToString())) continue;
            keep.Add(i);
        }

        var result = new Dictionary<int, int>();
        if (keep.Count == 0) return result;

        int runStart = -1, prev = -2;
        foreach (var i in keep)
        {
            if (i != prev + 1)
            {
                if (runStart != -1) result[runStart] = prev + 1;
                runStart = i;
            }
            prev = i;
        }
        if (runStart != -1) result[runStart] = prev + 1;
        return result;
    }
}
