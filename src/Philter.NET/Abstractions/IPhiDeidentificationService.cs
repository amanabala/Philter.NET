// De-identification logic ported from Philter (philter-ucsf / philter-lite,
// fork: amanabala/philter-lite).
// Copyright (c) 2018, REGENTS OF THE UNIVERSITY OF CALIFORNIA. BSD 3-Clause License.
// Citation: Norgeot et al., npj Digital Medicine 3, 57 (2020).
// https://doi.org/10.1038/s41746-020-0258-y

using Philter.NET;

namespace Philter.NET;

/// <summary>
/// Produces a de-identified working copy of a clinical note (HIPAA Safe Harbor — 18
/// identifiers) suitable for transmission to an external AI service. The original note
/// is NEVER mutated and the de-identified copy is ephemeral — callers must NOT persist it.
/// </summary>
public interface IPhiDeidentificationService
{
    /// <summary>
    /// De-identifies <paramref name="clinicalNote"/> and returns the scrubbed copy plus
    /// category counts. The de-identified copy is ephemeral — callers must NOT persist it.
    /// </summary>
    Task<DeidentificationResult> DeidentifyAsync(string clinicalNote);

    /// <summary>
    /// As <see cref="DeidentifyAsync"/>, but the result additionally carries
    /// <see cref="DeidentificationResult.Spans"/> — the position + category of every redacted
    /// run (offsets relative to <paramref name="clinicalNote"/>). The spans contain NO original
    /// text by design; a caller that needs the original token slices it from its own copy of
    /// <paramref name="clinicalNote"/>. Intended for redaction visualization, coverage/QA, and
    /// task-specific restore decisions made entirely on the caller's side.
    /// </summary>
    Task<DeidentificationResult> DeidentifyWithSpansAsync(string clinicalNote);
}
