# Philter.NET

A C# port of [Philter-Lite](https://github.com/UCSF-Pediatrics/philter-lite),
a rule-based PHI de-identification engine for clinical free text. Targets
HIPAA Safe Harbor-style redaction in .NET applications that need to scrub
clinical notes before sending them to third-party LLM APIs (or anywhere else
PHI shouldn't go).

> **Status:** Pre-release, not yet on NuGet. Validated against a **synthetic**
> gold corpus — **not real patient data** (see [Recall & limitations](#recall--limitations)).
> The public API is still settling; expect breaking changes before 1.0.

## Why

If you're building healthcare software in .NET and want to send clinical
documentation to an LLM, you need a PHI de-identification step in front of
that boundary. There's no good native option today — most teams either:

1. Shell out to a Python service (operational overhead, latency, deployment friction).
2. Roll their own regex pipeline (inevitably misses real PHI shapes).
3. Skip de-identification and pray (don't do this).

Philter.NET is option 4: a faithful C# port of an academically-developed
filter pipeline, with a pluggable POS tagger so you can choose your
tokenizer.

## Performance

Measured 2026-06-16 on an idle dev box, warm steady-state:

| Engine | Median ms | Notes |
|---|---|---|
| philter-lite (Python, upstream) | 51 | reference impl |
| **Philter.NET (this project)** | **20** | NonBacktracking regex engine, .NET 10 |

Cold start (first call after process boot) is **sub-second** with the default
perceptron tagger — its ~5.5 MB model parses quickly, leaving regex JIT as the
main cost. (An earlier heavier tagger incurred a ~14 s cold start on Azure App
Service B2; the perceptron default removes it.) Pre-warming the singleton is
optional.

## Recall & limitations

**Read this before relying on Philter.NET for anything that matters.**

### Recall benchmark

Recall (the fraction of true PHI that gets redacted — a miss is a leak) is
measured against a **synthetic** gold corpus of 200 LLM-generated clinical
notes (2,740 labelled PHI spans) where every identifier is injected
programmatically, so the ground-truth spans are exact. The generator and
harness live in the project tooling.

| Category | Recall |
|---|---|
| Address, MRN, Phone | 100% |
| Date | 99.6% |
| Name | 95.8% (97.3% with `.AddNerNameDetection()`) |
| **Overall** | **97.9% (98.6% with NER)** |

The clinical sidecar holds recall flat while reducing over-redaction
(higher precision). Optional NER name detection
(`.AddNerNameDetection()`, off by default) recovers some — not all — of the
name gap; see below.

### Limitations — please read

- **Not validated on real patient data.** Every number above is on
  *synthetic* notes. Synthetic PHI is cleaner and more regular than real-world
  PHI (typos, OCR noise, idiosyncratic formats, nicknames). **You must
  validate on your own representative data before trusting this in
  production.** We deliberately do *not* benchmark on gated real-patient
  corpora (i2b2/n2c2, PhysioNet) here.
- **The name gap is fundamental, not a bug.** ~95.8% name recall; the misses
  are overwhelmingly surnames that are also common English words (`Read`,
  `Young`, `Stone`, `Fields`). A rule/gazetteer pipeline cannot catch these
  without redacting the everyday words too (which would wreck precision).
  `.AddNerNameDetection()` (statistical NER) recovers ~⅓ of them; the rest
  still leak. If your text is name-dense, layer additional name detection.
- **Rule-based, not a learned de-identifier.** It will miss PHI shapes its
  rules don't anticipate. It is a strong *filter*, not a guarantee.
- **Over-redaction is expected and is the safe failure mode.** The sidecar
  reduces it, but you will still see some legitimate clinical text redacted.
- **No certification or warranty.** Philter.NET is not a HIPAA-compliance
  product, has not been independently audited, and is provided "as is" (see
  [LICENSE](LICENSE)). De-identification is *your* legal responsibility;
  this is a tool to help, not a sign-off.

## Packages

- `Philter.NET` — core filter engine, configuration loader, abstractions.
- `Philter.NET.PerceptronTagger` — **recommended `IPosTagger` (default).** A pure-managed,
  ~5.5 MB faithful port of nltk's averaged-perceptron tagger (the tagger philter-lite itself
  uses). No native dependencies, no network. In our benchmark it gives *slightly higher*
  recall than the Catalyst tagger at ~1/18th the footprint.
- `Philter.NET.Clinical` — **the clinical-text sidecar.**
  A pre-built `PhilterSidecar` of safe patterns rescuing legitimate clinical
  text (BP readings, lab values, dose patterns, drug names, abbreviations,
  pain scales, etc.) from over-redaction. Developed against **synthetic**
  clinical encounter notes (originally inside a private healthcare-coding
  product, SmartCodingAI); released here so adopters don't have
  to rediscover the same edge cases. **This is the value layer — the bare
  port without it over-redacts common clinical text** (measured: it cuts
  over-redaction by an order of magnitude on a representative note).

The POS tagger is pluggable (`IPosTagger`). Use `Philter.NET.PerceptronTagger`
(lightweight, recommended) or implement `IPosTagger` with your own tagger. A
heavier Catalyst-based tagger with optional statistical (WikiNER) name detection
is planned as a **separate companion package** — the core, sidecar, and perceptron
tagger carry no native dependencies.

The Clinical package is split out because not every Philter.NET consumer
is doing healthcare work — non-clinical text doesn't need (and shouldn't
pay for) clinical-pattern matching. If you ARE doing healthcare, pull it.

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Philter.NET;
using Philter.NET.PerceptronTagger;
using Philter.NET.Clinical;  // optional — recommended if your text is clinical

services.AddPhilter()
        .AddClinicalSidecar()       // rescues common clinical text from over-redaction
        .AddPerceptronTagger();     // lightweight POS tagger (default recommendation)

// ...

var deid = serviceProvider.GetRequiredService<IPhiDeidentificationService>();
var result = await deid.DeidentifyAsync("Patient John Doe, DOB 1/15/1965, BP 142/88.");

Console.WriteLine(result.DeidentifiedNote);
// "Patient [PHI_0001], DOB [DATE_0001], BP 142/88."

Console.WriteLine($"{result.Stats.TotalReplacements} replacements in {result.Stats.ProcessingMs} ms.");
```

On a representative clinical encounter, `AddClinicalSidecar()` reduces
over-redaction by an order of magnitude — see
`Philter.NET.Tests/ClinicalSidecarTests.cs` for the regression-tested
baseline-vs-sidecar comparison and the per-edge-case assertions.

## Sidecar patterns

`AddClinicalSidecar()` (shown above) registers the ready-made `Philter.NET.Clinical`
sidecar. The same mechanism also lets you add **your own** domain-specific safe
patterns — for a specialty or note shape the clinical sidecar doesn't cover — from
your own embedded resource or on-disk JSON, without touching the base config:

```csharp
// Your own custom safe patterns, alongside (or instead of) AddClinicalSidecar().
services.AddPhilter()
        .AddSidecar(typeof(MyApp).Assembly, "MyApp.PhiFilters.specialty_safe.json");
```

Sidecar patterns prepend to the upstream pipeline — they claim spans *before* any
base filter can, so you can rescue legitimate clinical text (`ratio`,
`microalbumin`, `well-appearing`) that a permissive base filter might otherwise tokenize.

## How this project is developed

Philter.NET is developed by a single maintainer with substantial AI
assistance (Claude). Architectural decisions, API design, scoping, and
review are mine. Routine code-writing and test scaffolding are AI-assisted.
Bug reports and PRs are reviewed by a human before merge. This disclosure
is upfront so adopters running their own dependency-review process can
factor it in.

## License

[BSD 3-Clause](LICENSE) — same as upstream Philter-Lite. See
[NOTICE](NOTICE) for upstream attribution.

## Acknowledgments

This is a C# port. The hard work of designing the filter pipeline, the
phi-type taxonomy, and the lookup sets was done by the UCSF Pediatrics
team. Bugs in this port are mine; the core idea is theirs.
