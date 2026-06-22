// De-identification logic ported from Philter (philter-ucsf / philter-lite).
// Copyright (c) 2018, REGENTS OF THE UNIVERSITY OF CALIFORNIA. BSD 3-Clause License.

namespace Philter.NET;

public class DeidentificationResult
{
    public string DeidentifiedNote { get; init; } = string.Empty;
    public DeidentificationStats Stats { get; init; } = new();
    public bool PhiDetected => Stats.TotalReplacements > 0;

    /// <summary>
    /// Per-redaction span metadata, populated only by
    /// <see cref="IPhiDeidentificationService.DeidentifyWithSpansAsync"/> (empty otherwise).
    /// Reports <em>where</em> and <em>what category</em> was redacted — by design it does
    /// NOT carry the original (PHI) text. A caller that holds the original note can slice
    /// <c>note.Substring(span.Start, span.Stop - span.Start)</c> itself if it needs the text.
    /// </summary>
    public IReadOnlyList<RedactionSpan> Spans { get; init; } = Array.Empty<RedactionSpan>();
}

/// <summary>
/// Coordinate + classification metadata for one redacted run. Offsets are relative to the
/// <em>input</em> note passed to <see cref="IPhiDeidentificationService.DeidentifyWithSpansAsync"/>.
/// <para>
/// Deliberately carries no original-text payload: a PHI de-identifier should never hand the
/// removed PHI back through its public API (an easy way for adopters to accidentally re-expose
/// it). Reconstruction of the original token — when legitimately needed, e.g. to evaluate
/// whether a redaction should be restored for a downstream task — is the caller's
/// responsibility, performed against its own copy of the input.
/// </para>
/// </summary>
public sealed class RedactionSpan
{
    /// <summary>Inclusive start offset, relative to the input note.</summary>
    public int Start { get; init; }

    /// <summary>Exclusive stop offset, relative to the input note.</summary>
    public int Stop { get; init; }

    /// <summary>The HIPAA-style category bucket the run was attributed to.</summary>
    public StatsCategory Category { get; init; }

    /// <summary>
    /// Title of the filter that claimed the run, or <c>null</c> when the run was redacted by
    /// default-deny (no filter matched).
    /// </summary>
    public string? Filter { get; init; }
}

public class DeidentificationStats
{
    public int NamesReplaced { get; set; }
    public int DatesReplaced { get; set; }
    public int PhonesReplaced { get; set; }
    public int AddressesReplaced { get; set; }
    public int MrnsReplaced { get; set; }
    public int SsnsReplaced { get; set; }
    public int EmailsReplaced { get; set; }
    public int UrlsReplaced { get; set; }
    public int OtherReplaced { get; set; }
    public bool AgeAggregated { get; set; }
    public long ProcessingMs { get; set; }

    public int TotalReplacements =>
        NamesReplaced + DatesReplaced + PhonesReplaced + AddressesReplaced +
        MrnsReplaced + SsnsReplaced + EmailsReplaced + UrlsReplaced + OtherReplaced;
}
