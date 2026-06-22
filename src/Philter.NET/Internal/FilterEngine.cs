// De-identification logic ported from Philter (philter-ucsf / philter-lite),
// translated from philter.py detect_phi() and its helpers.
// Copyright (c) 2018, REGENTS OF THE UNIVERSITY OF CALIFORNIA. BSD 3-Clause License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Philter.NET;

/// <summary>
/// Applies the ordered filter pipeline to a text, producing the include map (whitelist
/// coverage) and exclude map (PHI coverage), plus per-filter span attribution so the
/// orchestrator can build category tokens and statistics.
///
/// Mirrors Philter's detect_phi semantics exactly:
///   - filters run in pipeline order
///   - exclude=false ("safe") filters add to include_map if not already excluded
///   - exclude=true  ("phi")  filters add to exclude_map if not already included
///   - regex_context uses the live include_map to decide context adjacency
/// </summary>
internal sealed class FilterEngine
{
    private static readonly Regex NonAlnumGroup = new(@"[^a-zA-Z0-9]+", RegexOptions.Compiled);

    private readonly IReadOnlyList<PhilterFilter> _filters;
    private readonly IPosTagger _posTagger;
    private readonly ILogger _logger;

    /// <summary>Tracks filter titles whose pattern has already timed out at least
    /// once, so we only log the offender on first sighting instead of spamming
    /// the log on every request.</summary>
    private readonly HashSet<string> _timedOutFilters = new(StringComparer.Ordinal);
    private readonly object _timedOutSync = new();

    public FilterEngine(IReadOnlyList<PhilterFilter> filters, IPosTagger posTagger, ILogger? logger = null)
    {
        _filters = filters;
        _posTagger = posTagger;
        _logger = logger ?? NullLogger.Instance;
    }

    public DetectionResult Detect(string text, IEnumerable<(int start, int stop)>? preIncluded = null)
    {
        var includeMap = new CoordinateMap();
        if (preIncluded is not null)
        {
            foreach (var (s, e) in preIncluded) includeMap.AddExtend(s, e);
        }
        var excludeMap = new CoordinateMap();
        var excludeAttribution = new List<AttributedSpan>();
        var perFilterCoords = new Dictionary<string, CoordinateMap>(_filters.Count);

        // POS tag once (cheap and shared by set + pos_matcher filters)
        var posTokens = _posTagger.Tag(text);

        foreach (var f in _filters)
        {
            var hits = new CoordinateMap();
            try
            {
                switch (f.Type)
                {
                    case "regex":
                        MapRegex(text, f.CompiledPattern!, hits);
                        break;
                    case "set":
                        MapSet(text, posTokens, f, hits);
                        break;
                    case "pos_matcher":
                        MapPos(text, posTokens, f, hits);
                        break;
                    case "regex_context":
                        MapRegexContext(text, f, includeMap, perFilterCoords, hits);
                        break;
                    case "match_all":
                        hits.Add(0, text.Length, overlap: true);
                        break;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A filter whose pattern catastrophically backtracks on this input.
                // Drop that filter's contribution for this note (recall stays
                // conservative — anything it would have caught falls through to
                // default-deny in PhilterDeidentificationService.Transform) and log
                // the offender on first sighting so we can fix the pattern at the
                // source. The rest of the pipeline continues normally.
                hits = new CoordinateMap();
                bool firstSighting;
                lock (_timedOutSync) firstSighting = _timedOutFilters.Add(f.Title);
                if (firstSighting)
                {
                    _logger.LogWarning(
                        "PHI filter '{Title}' (type={Type}) exceeded the {Ms}ms per-filter match budget; " +
                        "skipping its contribution for this note. Subsequent timeouts of the same filter will not be logged.",
                        f.Title, f.Type, PhilterConfigLoader.PerFilterMatchTimeout.TotalMilliseconds);
                }
            }
            perFilterCoords[f.Title] = hits;

            // Resolve into include vs exclude per Philter's _get_exclude_include_maps.
            if (f.Type != "regex_context")
            {
                foreach (var (s, e) in hits.FileCoords())
                {
                    if (f.Exclude)
                    {
                        if (!includeMap.DoesOverlap(s, e))
                        {
                            excludeMap.AddExtend(s, e);
                            excludeAttribution.Add(new AttributedSpan(s, e, f));
                        }
                    }
                    else
                    {
                        if (!excludeMap.DoesOverlap(s, e))
                            includeMap.AddExtend(s, e);
                    }
                }
            }
            else
            {
                // regex_context filters override prior decisions.
                foreach (var (s, e) in hits.FileCoords())
                {
                    if (f.Exclude)
                    {
                        excludeMap.AddExtend(s, e);
                        includeMap.Remove(s, e);
                        excludeAttribution.Add(new AttributedSpan(s, e, f));
                    }
                    else
                    {
                        includeMap.AddExtend(s, e);
                        excludeMap.Remove(s, e);
                    }
                }
            }
        }

        return new DetectionResult(includeMap, excludeMap, excludeAttribution);
    }

    private static void MapRegex(string text, Regex regex, CoordinateMap hits)
    {
        foreach (Match m in regex.Matches(text))
        {
            if (!m.Success || m.Length == 0) continue;
            hits.AddExtend(m.Index, m.Index + m.Length);
        }
    }

    private static void MapSet(string text, IReadOnlyList<TaggedToken> tokens, PhilterFilter f, CoordinateMap hits)
    {
        var data = f.SetValues!;
        var posFilter = f.PosFilter; // null = no POS gating
        foreach (var t in tokens)
        {
            // POS gating is applied at the token level (Catalyst tags whole tokens).
            // The only POS-gated set filter is none today (nonames has pos=[]); kept for fidelity.
            if (posFilter is not null && !posFilter.Contains(t.Pos)) continue;

            // Replicate upstream philter-lite's _get_clean / _map_set: split the token's text on
            // non-alphanumeric characters into alnum sub-tokens and match EACH against the set,
            // whitelisting its exact span. This is the port-fidelity fix — Catalyst keeps
            // compounds like "Well-appearing" / "8-year-old" / "DM/renal" as single tokens, so the
            // old concatenated-clean ("wellappearing") never matched nonames and the whole compound
            // was default-denied. Upstream splits on the hyphen/slash and whitelists "well",
            // "appearing", "year", "old", "renal", etc. individually.
            int tStop = Math.Min(t.Stop, text.Length);
            int i = Math.Max(0, t.Start);
            while (i < tStop)
            {
                if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
                int s = i;
                while (i < tStop && char.IsLetterOrDigit(text[i])) i++;
                var sub = text.Substring(s, i - s);
                if (data.Contains(sub.ToLowerInvariant()) || data.Contains(sub))
                    hits.AddExtend(s, i);
            }
        }
    }

    private static void MapPos(string text, IReadOnlyList<TaggedToken> tokens, PhilterFilter f, CoordinateMap hits)
    {
        var posFilter = f.PosFilter;
        if (posFilter is null) return;
        bool cd = posFilter.Contains("CD"); // the only pos_matcher today is the CD (number) whitelist
        foreach (var t in tokens)
        {
            // Whole-token POS match (e.g. a lone number Catalyst tagged CD).
            if (posFilter.Contains(t.Pos)) { hits.AddExtend(t.Start, t.Stop); continue; }
            if (!cd) continue;

            // CD fidelity fix: upstream runs pos_tag over non-alnum-split sub-tokens, so a number
            // inside a compound ("8-year-old"->8, "1.73m"->73) is tagged CD and whitelisted. Catalyst
            // tags whole tokens, so we split here and whitelist PURELY-NUMERIC sub-tokens as CD.
            // Capped at <=4 digits — conservative: covers ages/scores/years/small counts but never
            // 5+ digit zip/MRN/account space. RECALL-SAFE because POS MATCHER is the LAST filter
            // (index ~311), so every PHI number/date/SSN/phone/zip exclude has already claimed its
            // span in excludeMap; the include resolution (!excludeMap.DoesOverlap) skips those.
            int tStop = Math.Min(t.Stop, text.Length);
            int i = Math.Max(0, t.Start);
            while (i < tStop)
            {
                if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
                int s = i; bool allDigit = true;
                while (i < tStop && char.IsLetterOrDigit(text[i])) { if (!char.IsDigit(text[i])) allDigit = false; i++; }
                if (allDigit && (i - s) <= 4) hits.AddExtend(s, i);
            }
        }
    }

    private static void MapRegexContext(
        string text,
        PhilterFilter f,
        CoordinateMap includeMap,
        Dictionary<string, CoordinateMap> perFilterCoords,
        CoordinateMap hits)
    {
        var regex = f.CompiledPattern!;
        var context = f.Context ?? "all";
        var contextFilter = f.ContextFilter ?? "all";

        Dictionary<int, int> excludeReference;
        if (string.Equals(contextFilter, "all", StringComparison.Ordinal))
        {
            excludeReference = includeMap.GetComplement(text);
        }
        else if (perFilterCoords.TryGetValue(contextFilter, out var referenced))
        {
            excludeReference = referenced.FileCoords().ToDictionary(x => x.start, x => x.stop);
        }
        else
        {
            excludeReference = new Dictionary<int, int>();
        }

        var phiStarts = new HashSet<int>(excludeReference.Keys);
        var phiEnds = new HashSet<int>(excludeReference.Values);

        foreach (Match m in regex.Matches(text))
        {
            if (!m.Success || m.Length == 0) continue;
            int s = m.Index, e = m.Index + m.Length;
            bool phiLeft = phiEnds.Contains(s);
            bool phiRight = phiStarts.Contains(e);
            bool keep = context switch
            {
                "left" => phiLeft,
                "right" => phiRight,
                "left_or_right" => phiLeft || phiRight,
                "left_and_right" => phiLeft && phiRight,
                _ => false,
            };
            if (!keep) continue;

            // Tokenize the match: skip leading punctuation per Philter.
            int trackStart = s;
            while (trackStart < e && CoordinateMap.PunctuationMatcher.IsMatch(text[trackStart].ToString()))
                trackStart++;
            if (trackStart < e) hits.AddExtend(trackStart, e);
        }
    }
}

internal sealed record DetectionResult(
    CoordinateMap IncludeMap,
    CoordinateMap ExcludeMap,
    IReadOnlyList<AttributedSpan> ExcludeAttribution);

internal sealed record AttributedSpan(int Start, int Stop, PhilterFilter Filter);
