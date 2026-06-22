using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Philter.NET;
using Philter.NET.PerceptronTagger;
using Philter.NET.Clinical;
using Xunit;

namespace Philter.NET.Tests;

/// <summary>
/// End-to-end tests for the Philter.NET.Clinical package against the same
/// clinical-note inputs that originally drove each sidecar version. These
/// serve as regression coverage so a future sidecar edit can't quietly
/// reintroduce a previously-fixed over-redaction.
/// </summary>
public class ClinicalSidecarTests
{
    private const string V16ReproNote = """
        SUBJECTIVE: 52 y/o male presents for medication refill visit. Reports HTN and DM2 are stable per home monitoring. Home BP readings 128-134/78-82. Fasting glucose 105-118. No new complaints. Tolerating all medications well. No chest pain, SOB, edema, or vision changes. Diet compliance has been good. Walking 30 minutes daily. Current medications: lisinopril 20mg daily, metformin 1000mg BID, atorvastatin 40mg daily, aspirin 81mg. Allergies: NKDA.

        OBJECTIVE: Vitals: Temp 98.4F, HR 72, RR 14, BP 132/82, Wt 175 lbs (stable). General: Well-appearing male in NAD. HEENT: Unremarkable. Lungs: CTA bilat. Heart: RRR, no murmur. Extremities: No edema. Neuro: Grossly intact.

        ASSESSMENT: 1. Essential hypertension — stable on current regimen. 2. Type 2 diabetes — stable per home monitoring, labs ordered for confirmation. Medication refills provided.

        PLAN:
        1. Refill all current medications: lisinopril 20mg #90, metformin 1000mg #180, atorvastatin 40mg #90, aspirin 81mg (OTC)
        2. Labs ordered: A1c, BMP, lipid panel, microalbumin/Cr ratio — draw 1 week before next visit
        3. Continue home BP and glucose monitoring
        4. Continue diet and exercise program
        5. Return in 3 months for lab review and comprehensive follow-up
        """;

    private static IServiceProvider BuildServicesWithoutClinical()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddPhilter().AddPerceptronTagger();
        return services.BuildServiceProvider();
    }

    private static IServiceProvider BuildServicesWithClinical()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddPhilter()
                .AddClinicalSidecar()
                .AddPerceptronTagger();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task RealRefillEncounter_clinical_sidecar_dramatically_reduces_over_redaction()
    {
        // The exact note that drove clinical sidecar v1.6 (from SmartCodingAI).
        // Goal: demonstrate the sidecar's VALUE — it rescues legitimate clinical text the bare
        // pipeline over-redacts. With the perceptron tagger (default) the baseline over-redacts
        // ~5 spans here and the sidecar drives it to ~0. Runs are deterministic (NonBacktracking
        // patterns run uncapped + warmed at load), so the margins are comfortable.

        using var baselineSp = (ServiceProvider)BuildServicesWithoutClinical();
        var baselineDeid = baselineSp.GetRequiredService<IPhiDeidentificationService>();
        var baseline = await baselineDeid.DeidentifyAsync(V16ReproNote);

        using var sp = (ServiceProvider)BuildServicesWithClinical();
        var deid = sp.GetRequiredService<IPhiDeidentificationService>();
        var withSidecar = await deid.DeidentifyAsync(V16ReproNote);

        // The marketing claim: an order of magnitude fewer redactions on a
        // representative clinical note. Empirically the baseline produces ~10
        // redactions on this note; with the sidecar that drops to 0-2 depending
        // on whether any sidecar filter hit a timeout. Assert >= 5x reduction.
        // Value demonstration: the bare baseline over-redacts this note modestly (the faithful
        // perceptron tagger already over-redacts less than the old Catalyst tagger did), and the
        // clinical sidecar rescues essentially all of it.
        Assert.True(baseline.Stats.TotalReplacements >= 3,
            $"Expected the bare baseline to over-redact this clinical note (signal guard); " +
            $"got {baseline.Stats.TotalReplacements}. If this drops, the test loses signal.");
        Assert.True(withSidecar.Stats.TotalReplacements <= 1,
            $"Sidecar should rescue essentially all of this note's over-redaction " +
            $"(baseline={baseline.Stats.TotalReplacements}, with sidecar={withSidecar.Stats.TotalReplacements}). " +
            $"De-identified output:\n{withSidecar.DeidentifiedNote}");
    }

    [Fact]
    public async Task DeidentifyWithSpans_reports_input_offsets_and_carries_no_phi_text()
    {
        using var sp = (ServiceProvider)BuildServicesWithClinical();
        var deid = sp.GetRequiredService<IPhiDeidentificationService>();
        const string note = "Patient Jane Doe, MRN 7654321, seen today.";

        var result = await deid.DeidentifyWithSpansAsync(note);

        Assert.NotEmpty(result.Spans);
        // Every span's offsets are valid against the INPUT note, and the spans expose no text.
        foreach (var s in result.Spans)
        {
            Assert.InRange(s.Start, 0, note.Length);
            Assert.InRange(s.Stop, s.Start, note.Length);
            Assert.DoesNotContain("OriginalText", s.GetType().GetProperties().Select(p => p.Name));
        }
        // Slicing the input at a span yields the redacted token — the MRN was redacted.
        Assert.Contains(result.Spans, s => note.Substring(s.Start, s.Stop - s.Start) == "7654321");
        // The de-identified output no longer contains the MRN.
        Assert.DoesNotContain("7654321", result.DeidentifiedNote);
        // The plain entry point carries no spans.
        var plain = await deid.DeidentifyAsync(note);
        Assert.Empty(plain.Spans);
    }

    [Theory]
    [InlineData("Headache is bilateral, pressure-like, rated 6/10.", "6/10")]
    [InlineData("Reports nausea 3/10 today, down from 8/10 yesterday.", "3/10")]
    [InlineData("Home BP readings 128-134/78-82.", "128-134/78-82")]
    [InlineData("Labs ordered: microalbumin/Cr ratio.", "microalbumin/Cr")]
    [InlineData("PLAN:\n1. Refill lisinopril\n2. Continue monitoring", "2. Continue")]
    // v1.7 (2026-06-18): digit-hyphen temporal compounds over-redacted across the
    // synthetic corpus (year 27x, month 14x, week 10x as default-deny). Catalyst keeps
    // "8-year-old" as one token, so the transform splits it and strands the unit word.
    // Ages <=89 are not PHI (>89 already aggregated upstream). Bare prose words already
    // survive via POS — the last row guards that they stay safe.
    [InlineData("Well-child examination, 8-year-old male.", "8-year-old")]
    [InlineData("Presents for routine 3-month follow-up.", "3-month")]
    [InlineData("Will order a 2-week event monitor.", "2-week")]
    [InlineData("Smoking: 15 pack-year history.", "pack-year")]
    [InlineData("10-year ASCVD risk: 5.2%.", "10-year")]
    [InlineData("Return in 1 year for annual wellness.", "1 year")]
    // v1.7 high-confidence batch (2026-06-18), driven by the corpus analyzer:
    [InlineData("Melanocytic nevi, unspecified (D22.9) — left calf lesion.", "D22.9")]
    [InlineData("Dermatitis, unspecified (L30.9) — eczema flare.", "L30.9")]
    [InlineData("Degenerative disc disease at L4-5 and L5-S1.", "L5-S1")]
    [InlineData("Active in soccer 2x/week and runs daily.", "2x")]
    [InlineData("Worry themes, no SI/HI, no AH/VH, no delusions.", "SI/HI")]
    [InlineData("Knee stable to varus/valgus stress. Negative McMurray.", "varus/valgus")]
    [InlineData("Crackles on left, extending to mid-lung fields.", "mid-lung")]
    [InlineData("PLAN: Punch biopsy (4mm) of left calf lesion.", "4mm")]
    [InlineData("Start topical tretinoin 0.025% cream nightly.", "0.025%")]
    // v1.8 (2026-06-18), driven by the full 2,989-note SyntheticNotes corpus pass.
    // DM/renal headline case (SmartCoding testing): whitelisting 'renal' should
    // transitively save 'DM' (the base Find Initials regex_context filter only fires
    // adjacent to PHI), so the whole compound must survive.
    [InlineData("Avoid NSAIDs given DM/renal considerations.", "DM/renal")]
    [InlineData("Avoid NSAIDs given DM/renal considerations.", "NSAIDs")]
    [InlineData("Creatinine 1.4 mg/dL, stable from baseline.", "mg/dL")]
    [InlineData("eGFR 42 mL/min/1.73m2 consistent with CKD.", "1.73m")]
    [InlineData("RR: 18 breaths/min, lungs clear.", "breaths")]
    [InlineData("Presented with a two-month history of cough.", "two-month")]
    [InlineData("Social history: 35-pack-year smoking history.", "35-pack-year")]
    [InlineData("Heart: regular rate, no murmurs, rubs, or gallops.", "murmurs")]
    [InlineData("Started goal-directed therapy; remains rate-controlled.", "goal-directed")]
    [InlineData("Tried over-the-counter ibuprofen without relief.", "over-the-counter")]
    [InlineData("Tenderness over the right shin and second finger.", "shin")]
    [InlineData("DEXA T-score -2.6 at the lumbar spine.", "T-score")]
    [InlineData("RACE/ETHNICITY: Hispanic or Latino.", "ETHNICITY")]
    [InlineData("12-Lead ECG performed today, normal sinus rhythm.", "Lead")]
    [InlineData("Smoked for forty-five years before quitting.", "forty-five")]
    // v1.9 (2026-06-18): a real neurocognitive-exam note concentrated low-frequency
    // over-redactions the corpus frequency ranking had buried. (CPT/ICD codes and neuro
    // eponyms like Romberg are handled consumer-side in SmartCoding's restore step.)
    [InlineData("MoCA administered: Score 14/30 (severely impaired).", "14/30")]
    [InlineData("Naming: 2/3 (impaired). Attention: 4/6 (impaired).", "2/3")]
    [InlineData("Cranial Nerves II-XII: Intact bilaterally.", "II-XII")]
    [InlineData("Acetaminophen as needed for pain/headache.", "pain/headache")]
    [InlineData("Judgment/Insight: poor insight into cognitive deficits.", "Judgment/Insight")]
    [InlineData("Visuospatial/Executive: 1/5 (impaired).", "Visuospatial/Executive")]
    [InlineData("Specifics unclear to patient/daughter.", "patient/daughter")]
    [InlineData("Patient's mother had cognitive decline in her 80s.", "80s")]
    [InlineData("Unable to subtract serial 7s from 100.", "7s")]
    [InlineData("Finger-to-nose test intact. No dysmetria.", "Finger-to-nose")]
    [InlineData("Unable to copy intersecting pentagons.", "intersecting pentagons")]
    // v1.10.1 (2026-06-18): safely-fixable families from the full per-note over-redaction catalog.
    [InlineData("Thyroid: TSH 3.2 mIU/L, within normal range.", "mIU/L")]
    [InlineData("CBC: WBC 6.8 K/uL, RBC 4.2.", "K/uL")]
    [InlineData("BMI: 22.3 kg/m2 (normal).", "kg/m")]
    [InlineData("Imaging ordered by PCP to r/o malignancy.", "r/o")]
    [InlineData("Soft, nontender; bowel sounds present in all quadrants.", "bowel")]
    [InlineData("Growth tracking at the 50th percentile for age.", "50th")]
    [InlineData("Screening negative for HIV and HCV.", "HIV")]
    [InlineData("Colon adenocarcinoma, stage 3b on staging workup.", "stage 3b")]
    // v1.10.4 (2026-06-18): final equilibrium batch — unambiguous English/medical words
    // not in nonames (no name/place-collision risk). Last sidecar iteration before
    // declaring equilibrium; remaining tail is deferred-by-design or recall-risky.
    [InlineData("Cataract impacting visual clarity on the left.", "impacting")]
    [InlineData("Findings necessitating urgent referral.", "necessitating")]
    [InlineData("MRI: multisequence protocol with honeycombing at the bases.", "honeycombing")]
    [InlineData("Audiometry threshold elevated 40 decibels at 4 kHz.", "decibels")]
    [InlineData("Fundoscopy reveals bilateral scotomas.", "scotomas")]
    [InlineData("Weak hip abductors and plantarflexors on exam.", "abductors")]
    // v1.10.6: the A1c recall fix must not break the legit A1c-value whitelist.
    [InlineData("Glycemic control poor: HbA1c 9.8% (target <7%).", "9.8%")]
    // v1.10.7 (2026-06-19): over-redactions from SmartCoding's sample-note-001. Base DATE
    // filters grabbed clinical number patterns the sidecar now claims first.
    [InlineData("Vital Signs: As documented above. BP elevated at 152/88.", "152/88")]
    [InlineData("mild claudication with ambulation beyond 2-3 blocks.", "2-3 blocks")]
    [InlineData("Patient sleeps 6-7 hours nightly.", "6-7 hours")]
    // v1.10.7: named clinical instrument — 'Montreal' is tagged a proper noun, but anchored to
    // 'Cognitive Assessment' it's the MoCA instrument, not a city/surname. Bare 'Montreal' is
    // NOT whitelisted (the pattern requires the full phrase), so no recall risk.
    [InlineData("Montreal Cognitive Assessment (MoCA) administered: score 14/30.", "Montreal Cognitive Assessment")]
    [InlineData("Reports recurrent blackouts and difficulty buttoning shirts.", "blackouts")]
    public async Task KnownEdgeCases_survive_redaction(string input, string mustRemain)
    {
        using var sp = (ServiceProvider)BuildServicesWithClinical();
        var deid = sp.GetRequiredService<IPhiDeidentificationService>();

        var result = await deid.DeidentifyAsync(input);

        Assert.Contains(mustRemain, result.DeidentifiedNote);
    }

    // RECALL guard — the inverse of the survive theory. The sidecar PREPENDS, so an
    // over-broad safe pattern can shadow a base PHI exclude and leak an identifier.
    // These rows assert real PHI is still removed even WITH the sidecar active.
    //
    // v1.10.3 (2026-06-18): the v1.9 "assessment score safe" pattern matched the date
    // slice "01/15" inside "01/15/2024" (denom 15 is a valid scale value); prepended,
    // it blocked the base date exclude and the DOB leaked. The fix added date-fragment
    // lookarounds. Surfaced by the hard-recall probe while gating the numeric/CD fix —
    // which also widened the blast radius (the year 2024 began surviving too). The first
    // row guards both: the full DOB must be removed.
    [Theory]
    [InlineData("DOB 01/15/2024. Follow-up scheduled.", "2024")]
    [InlineData("DOB 01/15/2024. Follow-up scheduled.", "01/15")]
    [InlineData("Date of birth: 03/22/1990, presents today.", "1990")]
    [InlineData("MRN 7654321 noted in the chart.", "7654321")]
    [InlineData("SSN 123-45-6789 on file.", "6789")]
    [InlineData("Phone (415) 555-0182 for callbacks.", "0182")]
    [InlineData("Lives at 12345 Main St, Boston MA 02101.", "02101")]
    // v1.10.5 (2026-06-18): first leak caught by the gold recall harness. A date ending a
    // sentence ("...flight 2021-07-09. The patient...") put a word boundary before the tail
    // "09", so the numbered-list-marker pattern matched "09. T" and shadowed the date exclude.
    [InlineData("Symptom onset 2021-07-09. The workup began promptly.", "2021-07-09")]
    [InlineData("Seen 03-11-2023. Follow-up arranged.", "03-11-2023")]
    // v1.10.6 (2026-06-18): gold-harness triage (200 notes). A date written right after a lab
    // keyword ("Last HbA1c 09-25-22: 9.8%") was absorbed by the A1c value matcher, shadowing
    // the date exclude. The date must redact; the A1c value is covered separately below.
    [InlineData("Last HbA1c 09-25-22: 9.8% today.", "09-25-22")]
    // v1.10.7: the BP-context and range-with-unit patterns must NOT shadow a real date that
    // happens to sit near a BP anchor or before a unit word (the v1.10.3/5/6 leak class).
    [InlineData("BP checked 01/15/2024 in clinic.", "01/15/2024")]
    [InlineData("Symptom onset 01-15-2024, worse over days.", "01-15-2024")]
    public async Task RecallCriticalPhi_is_redacted(string input, string mustNotRemain)
    {
        using var sp = (ServiceProvider)BuildServicesWithClinical();
        var deid = sp.GetRequiredService<IPhiDeidentificationService>();

        var result = await deid.DeidentifyAsync(input);

        Assert.DoesNotContain(mustNotRemain, result.DeidentifiedNote);
    }

    // The numeric/CD fix (core FilterEngine.MapPos, 2026-06-18): non-identifier numbers
    // inside compounds — the age in "8-year-old", small clinical counts/scores — are
    // tagged CD and preserved, while any number a PHI filter already claimed (the year in
    // a DOB, ZIP, MRN) is untouched because the CD matcher runs LAST and skips excluded
    // spans. These guard that the value numbers survive after the v1.10.3 recall fix.
    [Theory]
    [InlineData("An 8-year-old seen for a well-child visit.", "8")]
    [InlineData("Patient last seen in 2024 for follow-up.", "2024")]
    [InlineData("MoCA administered today: scored 14/30.", "14/30")]
    public async Task ValueNumbers_survive_after_recall_fix(string input, string mustRemain)
    {
        using var sp = (ServiceProvider)BuildServicesWithClinical();
        var deid = sp.GetRequiredService<IPhiDeidentificationService>();

        var result = await deid.DeidentifyAsync(input);

        Assert.Contains(mustRemain, result.DeidentifiedNote);
    }
}
