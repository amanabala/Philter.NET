// De-identification logic ported from Philter (philter-ucsf / philter-lite).
// Copyright (c) 2018, REGENTS OF THE UNIVERSITY OF CALIFORNIA. BSD 3-Clause License.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Philter.NET;

/// <summary>
/// Flat representation of one entry in philter_delta.json. The conversion script in
/// philter-lite resolves keyword indirection and macro interpolation at port time, so
/// every filter arrives self-contained.
/// </summary>
public sealed class PhilterFilter
{
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";   // regex | regex_context | set | pos_matcher | match_all
    [JsonPropertyName("exclude")] public bool Exclude { get; init; }     // true = PHI hit, false = whitelist hit
    [JsonPropertyName("phi_type")] public string PhiType { get; init; } = "OTHER";

    // regex / regex_context
    [JsonPropertyName("pattern")] public string? Pattern { get; init; }

    // regex_context only
    [JsonPropertyName("context")] public string? Context { get; init; }            // left | right | left_or_right | left_and_right
    [JsonPropertyName("context_filter")] public string? ContextFilter { get; init; } // "all" or a referenced filter title

    // set
    [JsonPropertyName("set_name")] public string? SetName { get; init; }

    // set / pos_matcher
    [JsonPropertyName("pos")] public List<string>? Pos { get; init; }

    // Lazily populated by the loader so we don't recompile per-call.
    [JsonIgnore] public Regex? CompiledPattern { get; set; }
    [JsonIgnore] public HashSet<string>? SetValues { get; set; }
    [JsonIgnore] public HashSet<string>? PosFilter { get; set; }

    /// <summary>
    /// Approximate stats category for the spec's <c>DeidentificationStats</c>. Philter's own
    /// taxonomy is finer than our 8-bucket counter; we collapse based on the upstream
    /// <c>phi_type</c> first and fall back to keyword matching on the title.
    /// </summary>
    public StatsCategory CategorizeForStats()
    {
        if (PhiType.Equals("DATE", StringComparison.OrdinalIgnoreCase))
            return StatsCategory.Date;

        var t = Title.ToLowerInvariant();
        if (t.Contains("phone") || t.Contains("fax")) return StatsCategory.Phone;
        if (t.Contains("ssn") || t.Contains("social security") ||
            t.StartsWith("ccc-dd-ddddd")) return StatsCategory.Ssn;
        if (t.Contains("mrn") || t.Contains("medical record")) return StatsCategory.Mrn;
        if (t.Contains("email") || t.Contains("@")) return StatsCategory.Email;
        if (t.Contains("url") || t.Contains("http") || t.Contains(" ip") || t.Contains("ipv")) return StatsCategory.Url;
        if (t.Contains("address") || t.Contains("street") || t.Contains("city") ||
            t.Contains("state") || t.Contains("zip") || t.Contains("county") ||
            t.Contains("hospital") || t.Contains("pharmacy") || t.Contains("admitted") ||
            t.Contains("lives in") || t.Contains("brought to") || t.Contains("waiting room") ||
            t.Contains("room") || t.Contains("floor") || t.Contains("box") ||
            t.Contains("location") || t.Contains("desk")) return StatsCategory.Address;
        if (t.Contains("name") || t.Contains("provider")) return StatsCategory.Name;
        return StatsCategory.Other;
    }
}

public enum StatsCategory
{
    Name, Date, Phone, Address, Mrn, Ssn, Email, Url, Other
}

public sealed class PhilterFilterFile
{
    [JsonPropertyName("filters")] public List<PhilterFilter> Filters { get; init; } = new();
}
