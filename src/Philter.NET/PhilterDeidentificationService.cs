// De-identification logic ported from Philter (philter-ucsf / philter-lite).
// Copyright (c) 2018, REGENTS OF THE UNIVERSITY OF CALIFORNIA. BSD 3-Clause License.
// Citation: Norgeot et al., npj Digital Medicine 3, 57 (2020).
// https://doi.org/10.1038/s41746-020-0258-y

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Philter.NET;

public sealed class PhilterDeidentificationService : IPhiDeidentificationService
{
    private readonly ILogger<PhilterDeidentificationService> _logger;
    private readonly FilterEngine _engine;
    private readonly INameRecognizer? _nameRecognizer;

    // Synthetic attribution for NER-detected names so they categorize as NAME in stats/spans.
    private static readonly PhilterFilter NerNameFilter = new()
    { Title = "ner name detector", PhiType = "NAME", Exclude = true };

    // Age aggregation per HIPAA Safe Harbor: anything > 89 must be reported as "90+".
    private static readonly Regex AgeOver89 = new(
        @"\b(9[0-9]|1\d{2,3})(\s*[-\s]?\s*(year[-\s]?old|y(ear)?s?[-\s]?old|y(ear)?s?o?|yo|yr|yrs))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PhilterDeidentificationService(
        ILogger<PhilterDeidentificationService> logger,
        IPhilterConfigLoader configLoader,
        IPosTagger posTagger,
        INameRecognizer? nameRecognizer = null)
    {
        _logger = logger;
        _engine = new FilterEngine(configLoader.Filters, posTagger, logger);
        _nameRecognizer = nameRecognizer;
    }

    /// <inheritdoc/>
    public Task<DeidentificationResult> DeidentifyAsync(string clinicalNote)
        => Task.FromResult(Run(clinicalNote, collectSpans: false));

    /// <inheritdoc/>
    public Task<DeidentificationResult> DeidentifyWithSpansAsync(string clinicalNote)
        => Task.FromResult(Run(clinicalNote, collectSpans: true));

    private DeidentificationResult Run(string clinicalNote, bool collectSpans)
    {
        if (string.IsNullOrWhiteSpace(clinicalNote))
            return new DeidentificationResult { DeidentifiedNote = clinicalNote ?? string.Empty };

        var sw = Stopwatch.StartNew();
        var stats = new DeidentificationStats();
        var spans = collectSpans ? new List<RedactionSpan>() : null;

        // Step 1 — age aggregation runs first because >89 ages must be replaced even when
        // surrounded by non-PHI clinical context (HIPAA Safe Harbor §164.514(b)(2)(i)(C)).
        // Returns, per substitution, the (aged, input) coordinate pairs so reported span
        // offsets can be translated back to the caller's original input string.
        var (aged, ageSubs) = AggregateAges(clinicalNote, stats);

        // Step 2 — filter pipeline. Builds include (whitelist) and exclude (PHI) maps.
        // The age-substituted spans (e.g. "90+yo") are pre-seeded as whitelisted so the
        // filter pipeline can't reach inside them and PHI-tokenize the "90" digit run.
        var detection = _engine.Detect(aged, ageSubs.Select(s => (s.agedStart, s.agedStop)));

        // Step 2b (opt-in) — NER name override. A registered INameRecognizer supplies
        // person-name spans that OVERRIDE the whitelist, recovering names the gazetteer
        // misses (notably common-word surnames like "Read"/"Young"/"Stone"). No-op when
        // no recognizer is registered, so default behavior is unchanged.
        if (_nameRecognizer is not null)
            detection = ApplyNameOverrides(detection, aged);

        // Step 3 — transform. Per Philter, anything outside the include_map and not punctuation
        // is considered PHI (default-deny). We replace contiguous PHI runs with category tokens,
        // labeling by the most specific attributed filter where one applies.
        var deidentified = Transform(aged, detection, stats, spans, ageSubs);

        sw.Stop();
        stats.ProcessingMs = sw.ElapsedMilliseconds;

        _logger.LogInformation(
            "PHI de-identification: {Total} replacements in {Ms}ms " +
            "(names={N}, dates={D}, phones={P}, addresses={A}, MRNs={M}, SSNs={S}, emails={E}, urls={U}, other={O}, ageAggregated={Age})",
            stats.TotalReplacements, stats.ProcessingMs,
            stats.NamesReplaced, stats.DatesReplaced, stats.PhonesReplaced,
            stats.AddressesReplaced, stats.MrnsReplaced, stats.SsnsReplaced,
            stats.EmailsReplaced, stats.UrlsReplaced, stats.OtherReplaced, stats.AgeAggregated);

        return new DeidentificationResult
        {
            DeidentifiedNote = deidentified,
            Stats = stats,
            Spans = (IReadOnlyList<RedactionSpan>?)spans ?? Array.Empty<RedactionSpan>(),
        };
    }

    /// <summary>
    /// Apply NER name spans as PHI overrides: drop them from the include (whitelist) map so the
    /// default-deny transform redacts them, add them to the exclude map, and attribute them as
    /// NAME. Runs after the filter pipeline so it beats the <c>nonames</c> whitelist.
    /// </summary>
    private DetectionResult ApplyNameOverrides(DetectionResult detection, string text)
    {
        var names = _nameRecognizer!.FindNames(text);
        if (names.Count == 0) return detection;

        var attribution = new List<AttributedSpan>(detection.ExcludeAttribution);
        foreach (var (start, stop) in names)
        {
            if (stop <= start || start < 0 || stop > text.Length) continue;
            detection.IncludeMap.Remove(start, stop);   // override the whitelist
            detection.ExcludeMap.AddExtend(start, stop);
            attribution.Add(new AttributedSpan(start, stop, NerNameFilter));
        }
        return detection with { ExcludeAttribution = attribution };
    }

    private static (string text, List<(int agedStart, int agedStop, int inputStart, int inputStop)> subs) AggregateAges(
        string text, DeidentificationStats stats)
    {
        var subs = new List<(int, int, int, int)>();
        var matches = AgeOver89.Matches(text);
        if (matches.Count == 0) return (text, subs);

        var sb = new StringBuilder(text.Length);
        int cursor = 0;
        foreach (Match m in matches)
        {
            sb.Append(text, cursor, m.Index - cursor);
            // Preserve the original unit suffix exactly (e.g. "yo", "year-old") so the
            // clinical reader still gets the same age-grouping signal.
            var replacement = "90+" + m.Groups[2].Value;
            int agedStart = sb.Length;
            sb.Append(replacement);
            subs.Add((agedStart, sb.Length, m.Index, m.Index + m.Length));
            cursor = m.Index + m.Length;
        }
        sb.Append(text, cursor, text.Length - cursor);
        stats.AgeAggregated = true;
        return (sb.ToString(), subs);
    }

    /// <summary>
    /// Translate an offset in the age-aggregated text back to the caller's original input.
    /// Age substitutions ("95yo" → "90+yo") change length; redaction runs never fall inside a
    /// substituted region (those are pre-whitelisted), so a cumulative length delta suffices.
    /// </summary>
    private static int AgedToInput(int agedOffset,
        List<(int agedStart, int agedStop, int inputStart, int inputStop)> subs)
    {
        if (subs.Count == 0) return agedOffset;
        int delta = 0;
        foreach (var s in subs)
        {
            if (s.agedStop <= agedOffset)
                delta += (s.inputStop - s.inputStart) - (s.agedStop - s.agedStart);
            else
                break; // subs are in order; later ones start after this offset
        }
        return agedOffset + delta;
    }

    /// <summary>
    /// Walks the text once. Tracks contiguous runs of PHI characters and emits a single
    /// category token per run, with numbered uniqueness so different spans can be told
    /// apart in the de-identified output. Whitelisted spans and punctuation pass through.
    /// </summary>
    private static string Transform(string text, DetectionResult detection, DeidentificationStats stats,
        List<RedactionSpan>? spans,
        List<(int agedStart, int agedStop, int inputStart, int inputStop)> ageSubs)
    {
        var includeMap = detection.IncludeMap;
        // Build an index from character position → most specific attribution filter for
        // accurate category labeling.
        var attribution = new PhilterFilter?[text.Length];
        foreach (var a in detection.ExcludeAttribution)
        {
            for (int i = a.Start; i < a.Stop && i < text.Length; i++)
                attribution[i] = a.Filter;
        }

        var sb = new StringBuilder(text.Length);
        var counters = new Dictionary<StatsCategory, int>();
        int i2 = 0;
        while (i2 < text.Length)
        {
            // Whitelisted span — keep verbatim
            if (includeMap.DoesExist(i2))
            {
                var (s, e) = includeMap.GetCoords(i2);
                sb.Append(text, s, e - s);
                i2 = e;
                continue;
            }

            char c = text[i2];
            if (CoordinateMap.PunctuationMatcher.IsMatch(c.ToString()))
            {
                sb.Append(c);
                i2++;
                continue;
            }

            // PHI run starts here. Consume until we hit a whitelist span or punctuation.
            int runStart = i2;
            PhilterFilter? runFilter = attribution[i2];
            while (i2 < text.Length &&
                   !includeMap.DoesExist(i2) &&
                   !CoordinateMap.PunctuationMatcher.IsMatch(text[i2].ToString()))
            {
                runFilter ??= attribution[i2];
                i2++;
            }

            var category = runFilter?.CategorizeForStats() ?? StatsCategory.Other;
            counters.TryGetValue(category, out var n);
            counters[category] = ++n;
            IncrementStat(stats, category);
            sb.Append('[').Append(CategoryToken(category)).Append('_')
              .Append(n.ToString("D4")).Append(']');

            // Span metadata (coordinates + category only — never the original PHI text).
            // Offsets are translated from the age-aggregated text back to the caller's input.
            // spans is null on the plain DeidentifyAsync path, so nothing is retained there.
            spans?.Add(new RedactionSpan
            {
                Start = AgedToInput(runStart, ageSubs),
                Stop = AgedToInput(i2, ageSubs),
                Category = category,
                Filter = runFilter?.Title,
            });
        }
        return sb.ToString();
    }

    private static void IncrementStat(DeidentificationStats s, StatsCategory cat)
    {
        switch (cat)
        {
            case StatsCategory.Name: s.NamesReplaced++; break;
            case StatsCategory.Date: s.DatesReplaced++; break;
            case StatsCategory.Phone: s.PhonesReplaced++; break;
            case StatsCategory.Address: s.AddressesReplaced++; break;
            case StatsCategory.Mrn: s.MrnsReplaced++; break;
            case StatsCategory.Ssn: s.SsnsReplaced++; break;
            case StatsCategory.Email: s.EmailsReplaced++; break;
            case StatsCategory.Url: s.UrlsReplaced++; break;
            default: s.OtherReplaced++; break;
        }
    }

    private static string CategoryToken(StatsCategory cat) => cat switch
    {
        StatsCategory.Name => "NAME",
        StatsCategory.Date => "DATE",
        StatsCategory.Phone => "PHONE",
        StatsCategory.Address => "ADDR",
        StatsCategory.Mrn => "MRN",
        StatsCategory.Ssn => "SSN",
        StatsCategory.Email => "EMAIL",
        StatsCategory.Url => "URL",
        _ => "PHI",
    };
}
