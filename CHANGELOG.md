# Changelog

User-visible changes, newest first, one line each. The card or issue id leads each line; per-session detail and
measurements are in [`SESSION_HANDOFF.md`](SESSION_HANDOFF.md), and open work is in [`TODO.md`](TODO.md).

## 2026-09-13 (afternoon): VAL-001 follow-ups

- **TOOL-012** `find_dead_code_candidates` no longer reports the program entry point as dead.
- **DIAG-003** `get_compilation_errors` applies the project's DiagnosticSuppressors by default. BPG.Data went from 13 false CS8618 errors to 0.
- **TOOL-014** `find_registrations` returns `unregisteredTypes` when a query matches no registration.
- **TOOL-014** DI registrations resolve inferred, `typeof`, factory and instance forms to real service and implementation types.
- **TOOL-014** Unregistered `BackgroundService` subclasses and registered generic types are no longer mistaken for each other.
- **TOOL-013** `find_dead_code_candidates` reports code used only by other dead code (`only-referenced-by-dead-code`, `keptAliveBy`).
- **TOOL-013** `find_dead_code_candidates` scans every eligible symbol, and `maxResults` caps the finished result.
- **TOOL-013** `registrationsWithOnlyDeadConsumers` lists DI registrations whose only constructor consumers are dead.
- **TOOL-013** Static constructors and finalizers are never reported as dead.
- **VAL-001** Real-session validation on BPG, report in `docs/acceptance/2026-09-13-val-001-bpg-session.md`.

## 2026-09-13 (morning): Codex review cards

- **DIAG-001** Both diagnostics tools can run the project's own analyzers (`includeAnalyzers`).
- **PERF-002** The `rename_symbol` preview returns changed spans instead of whole files, and `workspace_symbol` and `semantic_search` are capped.
- **WS-006, IDX-002** Reloads publish atomic generations, and indexed tools wait for their index.
- **TOOL-010** Line and column are validated before resolving a symbol (`POSITION_INVALID`).
- **IDX-003** A refresh keeps `BackgroundService` subclasses.
- **TOOL-009** Routes and hosted services must be the real extension methods, not same-named calls.
- **IDX-005** Same-named symbols in different assemblies stay distinct (`AMBIGUOUS_SYMBOL_ID`).
- **DIAG-002** `get_compilation_errors` reports `LoadFailures` and `PROJECT_NOT_FOUND`, and honours its filters.
- **TOOL-008** `rename_symbol` with `applyEdits` checks every file before writing any.
- **IDX-004** File changes are folded into both indexes once, atomically.
- **TOOL-011** `analyze_symbol` fills in implementations for members, and `list_document_symbols` lists indexers, delegates and enum members.
- **TOOL-011** A failed call sets MCP `isError`.
- **WS-007** Every tool result warns when the loaded workspace no longer matches the disk.
- **WS-005** Discovery prefers the solution with the most projects, and `reload_workspace(solutionPath)` switches solutions.

## 2026-08-15

- **TOOL-006** `find_dead_code_candidates` `includePublicTypes`: unreferenced public types, with DI-registered and framework-reached types suppressed.
- **TOOL-003** `find_dead_code_candidates` `Skipped` counters describe the whole solution, and `Truncated` was added.
- **TOOL-002** `find_registrations` counts only constructors as likely consumers.
- **TOOL-001** `project_overview` reports `TargetFramework` and `IsPackable`.
- **PERF-001 (#5)** `SymbolIndex` walks only the source assembly: 20–27× faster, with an identical index.
- **IDX-001** Symbols added after load show up in dead-code results without a reload.
- **WS-003** `WorkspaceLoadDiagnostic` carries `ProjectName`.
- **WS-004** Non-fatal MSBuild messages report as `ProjectLoadedWithWarnings`, not `Failure`.
- **WS-002** Fixed the obsolete `WorkspaceFailed` event; the build is warning-free.

## 2026-06-15

- **#1** `InvocationIndex` warm-up is about 2× faster end to end (the DI bind is gated behind a cheap name filter).

## 2026-06-04 / 2026-06-08

- Solution discovery also searches downward when none is found walking up.
- Tagged `v1.3.0`.

## 2026-05-21: v1.3

- New tools: `project_overview`, `find_entrypoints`, `find_registrations`, `find_callees`, `analyze_symbol`, `test_map` and `find_dead_code_candidates`.
- `format = "structured" | "summary"` on every tool.
- Diagnostics filters: `includeGenerated`, `excludeDiagnosticCodes` and `minimumSeverity`.
- **#4** `find_references` and `find_implementations` de-duplicate locations.

## 2026-05-16: v1.1 and v1.2

- **v1.1** Background warm-up pre-compilation, so the first query drops from ~8.4 s to ~1.9 s.
- **v1.1** `--log-file` flag.
- **v1.1** MSBuild load failures surface as `WorkspaceLoadDiagnostic`.
- **v1.2** `SymbolIndex` for `semantic_search` `has-attribute`/`returns`/`parameter-type`, about 1000× faster.

## 2026-05-15: v1

- MCP stdio server over Roslyn's `MSBuildWorkspace`, with mtime-based per-call refresh.
- Tools: `reload_workspace`, `list_document_symbols`, `workspace_symbol`, `goto_definition`, `hover`, `find_references`, `find_implementations`, `find_derived_types`, `find_callers`, `get_document_diagnostics`, `get_compilation_errors`, `semantic_search` and `rename_symbol` (preview by default).
- `--solution` argument, with discovery by walking up from the working directory.
- Self-contained win-x64 publish.
