// De-identification logic ported from Philter (philter-ucsf / philter-lite).
// Copyright (c) 2018, REGENTS OF THE UNIVERSITY OF CALIFORNIA. BSD 3-Clause License.

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Philter.NET;

public interface IPhilterConfigLoader
{
    IReadOnlyList<PhilterFilter> Filters { get; }
    IReadOnlyDictionary<string, HashSet<string>> Sets { get; }
}

/// <summary>
/// Loads philter_delta.json + sets/*.json from embedded resources, compiles regexes once,
/// and resolves set-name references. Singleton — never re-reads at runtime.
/// </summary>
public sealed class PhilterConfigLoader : IPhilterConfigLoader
{
    private const string ResourcePrefix = "Philter.NET.Configuration.";

    /// <summary>
    /// Match budget applied <em>only</em> to the small set of patterns that fall back
    /// to <see cref="RegexOptions.Compiled"/> because the <see cref="RegexOptions.NonBacktracking"/>
    /// engine rejected them (backreferences / lookarounds / atomic groups). Those are the
    /// only patterns that can catastrophically backtrack, so they're the only ones that
    /// need a wall-clock cap.
    /// <para>
    /// History: a 20 ms cap was previously applied to <em>every</em> pattern as a
    /// warm-up band-aid. That was wrong on two counts — NonBacktracking patterns are
    /// O(n) by construction and can never blow the budget (so the cap protected nothing),
    /// yet they routinely exceeded 20 ms on their <em>first</em> match while the engine
    /// builds its automaton. The result was healthy PHI filters being silently skipped on
    /// cold start (a recall/under-redaction risk), not catastrophic patterns being caught.
    /// We now give NonBacktracking patterns <see cref="Regex.InfiniteMatchTimeout"/>, warm
    /// every compiled pattern once at load (see the constructor), and keep a generous cap
    /// here for the genuinely-backtracking fallback set.
    /// </para>
    /// <see cref="FilterEngine"/> catches <see cref="RegexMatchTimeoutException"/>
    /// and continues with the rest of the pipeline so recall stays conservative.
    /// </summary>
    public static readonly TimeSpan PerFilterMatchTimeout = TimeSpan.FromSeconds(2);

    public IReadOnlyList<PhilterFilter> Filters { get; }
    public IReadOnlyDictionary<string, HashSet<string>> Sets { get; }

    public PhilterConfigLoader(
        ILogger<PhilterConfigLoader> logger,
        IEnumerable<PhilterSidecar>? sidecars = null)
    {
        var asm = typeof(PhilterConfigLoader).Assembly;

        // Discover set resources first so filters can be wired to their data.
        var sets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix + "sets.", StringComparison.Ordinal)) continue;
            // ...sets.<setname>.json
            var leaf = name.Substring((ResourcePrefix + "sets.").Length);
            if (!leaf.EndsWith(".json", StringComparison.Ordinal)) continue;
            var setName = leaf.Substring(0, leaf.Length - ".json".Length);
            using var s = asm.GetManifestResourceStream(name)!;
            var values = JsonSerializer.Deserialize<List<string>>(s) ?? new();
            sets[setName] = new HashSet<string>(values, StringComparer.Ordinal);
        }
        Sets = sets;

        var pipelineResource = ResourcePrefix + "philter_delta.json";
        using var ps = asm.GetManifestResourceStream(pipelineResource)
            ?? throw new InvalidOperationException(
                $"Embedded PHI filter pipeline resource '{pipelineResource}' not found. " +
                "Confirm Configuration/PhiFilters/philter_delta.json is included as an embedded resource.");
        var file = JsonSerializer.Deserialize<PhilterFilterFile>(ps)
            ?? throw new InvalidOperationException("philter_delta.json failed to deserialize");

        // Sidecars: consumer-registered safe patterns for domain-specific clinical
        // numerics not covered by upstream Philter (EF %, doses, vital signs, lab values).
        // PREPENDED so they fire before any PHI filter can claim a number-shaped span.
        // Sidecars are optional — with none registered, the upstream pipeline runs as-is.
        int sidecarAdded = 0;
        if (sidecars is not null)
        {
            foreach (var sidecarSource in sidecars)
            {
                using var ss = sidecarSource.OpenStream();
                if (ss is null)
                {
                    logger.LogWarning("Sidecar '{Label}' could not be opened; skipping.", sidecarSource.Label);
                    continue;
                }
                var sidecar = JsonSerializer.Deserialize<PhilterFilterFile>(ss);
                if (sidecar is { Filters.Count: > 0 })
                {
                    var combined = new List<PhilterFilter>(sidecar.Filters.Count + file.Filters.Count);
                    combined.AddRange(sidecar.Filters);
                    combined.AddRange(file.Filters);
                    file = new PhilterFilterFile { Filters = combined };
                    sidecarAdded += sidecar.Filters.Count;
                    logger.LogDebug("Sidecar '{Label}' prepended {Count} filters.", sidecarSource.Label, sidecar.Filters.Count);
                }
            }
        }

        int compiled = 0, nonBacktracking = 0, wired = 0, dropped = 0;
        var built = new List<PhilterFilter>(file.Filters.Count);
        foreach (var f in file.Filters)
        {
            try
            {
                switch (f.Type)
                {
                    case "regex":
                    case "regex_context":
                        if (string.IsNullOrEmpty(f.Pattern))
                        {
                            dropped++; continue;
                        }
                        // Philter's TOML patterns occasionally include Python-tolerated
                        // escape sequences that .NET's Regex parser rejects outright (e.g.
                        // `\_` in `month_name_dd`). Strip the backslash on the known cases
                        // — none of these characters have any regex-special meaning.
                        var pattern = SanitizePythonEscapes(f.Pattern);
                        // Prefer the NonBacktracking engine — it guarantees O(n) match time
                        // by construction, so the catastrophic-backtracking patterns we ship
                        // from upstream Philter (e.g. the giant alternation in `hospital safe`)
                        // can never blow the request budget. If a pattern uses features the
                        // NonBacktracking engine doesn't support (backreferences, lookarounds,
                        // atomic groups, balancing groups) the compile throws
                        // NotSupportedException and we fall back to Compiled + MatchTimeout
                        // — best-effort cap, but acceptable for the small set that needs it.
                        try
                        {
                            // NonBacktracking is O(n) by construction — it cannot run away, so
                            // no match cap is needed (and a tight one only mis-fires on the
                            // cold-start automaton build). Warmed once at load, below.
                            f.CompiledPattern = new Regex(pattern,
                                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                                Regex.InfiniteMatchTimeout);
                            nonBacktracking++;
                        }
                        catch (NotSupportedException)
                        {
                            f.CompiledPattern = new Regex(pattern,
                                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                                PerFilterMatchTimeout);
                            compiled++;
                        }
                        break;
                    case "set":
                        if (f.SetName == null || !sets.TryGetValue(f.SetName, out var data))
                        {
                            dropped++; continue;
                        }
                        f.SetValues = data;
                        if (f.Pos is { Count: > 0 })
                            f.PosFilter = new HashSet<string>(f.Pos, StringComparer.Ordinal);
                        wired++;
                        break;
                    case "pos_matcher":
                        if (f.Pos is { Count: > 0 })
                            f.PosFilter = new HashSet<string>(f.Pos, StringComparer.Ordinal);
                        break;
                    case "match_all":
                        break;
                    default:
                        dropped++;
                        continue;
                }
                built.Add(f);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Dropping unparseable PHI filter '{Title}' (type={Type})",
                    f.Title, f.Type);
                dropped++;
            }
        }
        Filters = built;

        // Warm every compiled regex once, here at load time, so the first real request
        // never pays the cold-start automaton-build / JIT cost that previously tripped the
        // (now removed) per-match cap on NonBacktracking patterns. Cheap — runs once per
        // process against a short throwaway input. The backtracking-fallback set keeps its
        // 2 s cap, so a pathological pattern can't hang warm-up either.
        var warmSw = System.Diagnostics.Stopwatch.StartNew();
        int warmed = 0;
        const string warmupProbe = "Patient 90yo seen 01/02/2026, MRN 1234567.";
        foreach (var f in built)
        {
            if (f.CompiledPattern is null) continue;
            try { f.CompiledPattern.IsMatch(warmupProbe); warmed++; }
            catch (RegexMatchTimeoutException) { /* backtracking fallback; will be skipped per-note too */ }
        }
        warmSw.Stop();

        logger.LogInformation(
            "Loaded {Total} PHI filters ({NonBack} non-backtracking + {Compiled} compiled-backtracking, " +
            "{Set} sets wired, {Dropped} dropped, {Sidecar} from clinical-numerics sidecar). " +
            "{SetCount} set dictionaries with {SetEntries} total entries. Warmed {Warmed} regexes in {WarmMs}ms.",
            built.Count, nonBacktracking, compiled, wired, dropped, sidecarAdded,
            sets.Count, sets.Values.Sum(s => s.Count), warmed, warmSw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Strip backslashes from escape sequences that Python's <c>re</c> tolerates
    /// but .NET's <c>Regex</c> rejects as <c>Unrecognized escape sequence</c>.
    /// Today only <c>\_</c> is known to occur in philter_delta; extend as new
    /// offenders surface (each addition logged so we don't silently mutate patterns).
    /// </summary>
    private static string SanitizePythonEscapes(string pattern)
    {
        return pattern.Replace("\\_", "_");
    }
}
