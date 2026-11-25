# Changelog — Foliant.DocumentExtraction

Experimental preview line, versioned separately from Foliant (tags `docext-v*`). See ADR-0009 and
API-STABILITY.md.

## 0.1.0-preview.1 — 2026-11-25

### Added
- `FoliantDocumentExtractionClient`: a local `DocumentExtractionClient` for PDFs, built on
  Foliant's default pipeline or any `IDocumentProcessor`.
- Mapping to `ExtractionDocument`: one section per page in reading order; headings, captions,
  footnotes, formulas, figures, tables with cell grids, and page headers and footers; coordinates
  in points from a top-left origin.
- Review signals in metadata (`foliant.pagesNeedingReview`, `foliant.sensitivityMarkedPages`,
  `foliant.pagesWithNotice`, `foliant.coverageHolds`, `foliant.recallPercent`) and per page, with the full `DocumentResult`
  as the raw representation.
- Input limit `MaxInputBytes` (default 512 MiB); per-call `ProcessingOptions` through
  `ProcessingOptionsKey`; unsupported options are rejected.
