# Changelog

All notable changes to Philter.NET are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) once
1.0.0 ships.

## [Unreleased]

## [0.1.0-preview] — 2026-06-22

First public preview of Philter.NET — a C# port of
[philter-lite](https://github.com/UCSF-Pediatrics/philter-lite) (UCSF Pediatrics,
BSD-3) for HIPAA Safe Harbor-style PHI de-identification of clinical free text.
Pre-publication: the API shape is real but may change before 1.0.0.

> **On the data:** all benchmarks here are on **synthetic** notes, not real
> patient data. See the README "Recall & limitations" section before relying on
> this for anything that matters.

### Packages

- **`Philter.NET`** — core filter engine, configuration loader, abstractions
  (`IPhiDeidentificationService`, `IPosTagger`, `INameRecognizer`,
  `IPhilterConfigLoader`), and the `PhilterSidecar` registration API. Embeds the
  upstream `philter_delta.json` pipeline + `firstnames`/`lastnames`/`nonames`
  lookup sets (BSD-3 derivative). Exposes a coordinates-only span API
  (`DeidentifyWithSpansAsync`) that returns offsets + categories but never the
  removed PHI text.
- **`Philter.NET.PerceptronTagger`** — **recommended `IPosTagger` (default).** A
  pure-managed, ~5.5 MB faithful port of nltk's averaged-perceptron tagger (the
  tagger philter-lite itself uses). No native dependencies, no network, Penn
  Treebank tags. Wire with `.AddPerceptronTagger()`.
- **`Philter.NET.Clinical`** — the clinical-text sidecar
  (`philter_clinical_safe.json` v1.10.7: 60 safe patterns covering BP readings,
  lab values, dose patterns, drug names, abbreviations, assessment scores,
  ranges-with-units, named instruments, etc.). Developed against **synthetic**
  clinical notes. Wire with `.AddClinicalSidecar()`. The bare port over-redacts
  common clinical text without it.
A heavier Catalyst-based `IPosTagger` with optional WikiNER-based name detection
(`INameRecognizer`) is **planned as a separate companion package** — kept out of this
repo so the core/sidecar/perceptron packages stay pure-managed with no native
dependencies. Most consumers should prefer the perceptron tagger regardless.

### Recall & precision

Measured against a 200-note **synthetic** gold corpus (2,740 labelled PHI spans,
generated so every identifier offset is exact):

| Category | Recall |
|---|---|
| Address, MRN, Phone | 100% |
| Date | 99.6% |
| Name | 95.8% (97.3% with optional NER) |
| **Overall** | **97.9% (98.6% with NER)** |

The clinical sidecar holds recall flat while cutting over-redaction. See the
README for the honest limitations (the name gap is bare common-word surnames;
the real fix is statistical NER, not gazetteer/sidecar changes).

### Performance

| Engine | Median ms | Cold start |
|---|---|---|
| philter-lite (Python, upstream) | 51 | — |
| **Philter.NET** (perceptron tagger) | **20** | sub-second |

The perceptron tagger removes the ~14 s Azure App Service B2 cold start that the
Catalyst model incurred. Production deployments should still pre-warm the singleton.

### Notes

- Public API surface is intentionally tight; filter-engine internals stay
  `internal` so they can iterate without breaking SemVer.
- Developed by a single maintainer with substantial AI assistance — see the
  README "How this project is developed" section.

[Unreleased]: https://github.com/amanabala/Philter.NET/compare/v0.1.0-preview...HEAD
[0.1.0-preview]: https://github.com/amanabala/Philter.NET/releases/tag/v0.1.0-preview
