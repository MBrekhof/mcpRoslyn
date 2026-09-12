# PERF-002 — per-tool response size (2026-09-13)

What each tool's default response costs the agent's context, measured against BPG (8 projects) through the real
stdio transport: `BenchmarkTests.Tool_response_sizes` launches the built server, calls every tool once as warm-up
and three times timed, and records the `content[*].text` the MCP client receives. Tokens ≈ chars / 4 (the same
convention as roslynk's benchmark). `structuredContent` was empty for every tool — the SDK sends text only.
Timings are warm medians on a machine that swings ~2x; read them as orders of magnitude, not claims.

Re-run: `dotnet test tests/mcpRoslyn.Tests --filter "FullyQualifiedName~BenchmarkTests.Tool_response_sizes" --logger "console;verbosity=detailed"`
(responses are dumped to `bin/Debug/net10.0/perf002/` for inspection).

## Results

| tool | scenario | before chars | after chars | ~tokens after | warm ms |
|---|---|---:|---:|---:|---:|
| `rename_symbol` (preview) | `ILLMService` → 16 occurrences in 8 files | **142 606** | **3 606** | 901 | ~300–460 |
| `workspace_symbol` | `query: "Spec"` | **43 066** | **8 942** | 2 235 | ~12–18 |
| `find_registrations` | defaults | 24 517 | 24 517 | 6 129 | ~7–11 |
| `semantic_search` | `parameter-type:BPG.Core.Models.Conversation` | 20 288 | 20 306 | 5 076 | ~6–11 |
| `find_callers` | `ILLMService.GenerateResponseAsync` | 5 568 | 5 568 | 1 392 | ~9–15 |
| `list_document_symbols` | `LLMService.cs` | 5 143 | 5 143 | 1 285 | ~7–12 |
| `get_compilation_errors` | defaults | 4 915 | 4 915 | 1 228 | ~50–76 |
| `analyze_symbol` | `ILLMService`, all sections | 3 539 | 3 539 | 884 | ~8–12 |
| `project_overview` | defaults | 3 297 | 3 297 | 824 | ~8–12 |
| `find_callees` | `LLMService.GenerateResponseAsync` | 3 110 | 3 110 | 777 | ~7–12 |
| `find_entrypoints` | defaults | 1 465 | 1 465 | 366 | ~7–10 |
| `find_dead_code_candidates` | defaults | 954 | 954 | 238 | ~345–590 |
| `find_references` | `IUnitOfWork` | 850 | 850 | 212 | ~13–16 |
| `hover` | `LLMService.cs:15:31` | 537 | 537 | 134 | ~6–11 |
| `find_implementations` | `ILLMService` | 414 | 414 | 103 | ~6–8 |
| `find_derived_types` | `IRepository<T>` | 349 | 349 | 87 | ~6–7 |
| `reload_workspace` | — | 327 | 327 | 81 | ~1 800–2 000 (once) |
| `goto_definition` | `LLMService.cs:15:31` | 154 | 154 | 38 | ~6–10 |
| `test_map` | `LLMService` | 79 | 79 | 19 | ~7–13 |
| `get_document_diagnostics` | `LLMService.cs` (analyzers on) | 29 | 29 | 7 | ~80–140 |

Total across the 20 calls: **261 207 → 88 101 chars** (~65k → ~22k tokens).

## What changed

- **`rename_symbol` preview was a bug, not a default.** Each "edit" carried the whole file before and after:
  `SourceText.GetTextChanges` only yields detailed changes when the new text was derived from the old one by text
  edits, and `Renamer` rebuilds the syntax tree, so it returned one whole-file change per document. It now uses
  `Document.GetTextChangesAsync(oldDocument)`, which diffs the trees — the changed spans, one per occurrence in this run. `applyEdits` was
  never affected (it writes the renamed document's full text). The existing preview test now pins `OldText == "Greet"`.
- **`workspace_symbol` default cap 100 → 25**, and the result gains `Truncated`. It used to stop at the cap
  silently; `Truncated` is set only when a further match existed, so a result exactly at the cap reads as complete.
- **`semantic_search` gains `maxResults` (default 50) and `Truncated`.** It was uncapped. This scenario (~47 matches)
  is under the cap and didn't change; the cap is for the unbounded case — `returns:string` or `parameter-type:string`
  on a large solution. A new parameter rather than a better default, because there was no default to change.

## Measured and deliberately not changed

- **JSON escaping** (`<` → `<` etc., System.Text.Json's default encoder) is 4% of all output, and 7% of the
  old rename preview that is now gone. Not worth changing the SDK's serializer options for.
- **`find_registrations`** (6.1k tokens) is 20 registrations, each with its raw call text and constructor consumers —
  that content is the tool's point, and `maxResults` already defaults to 20.
- **`SymbolInfo` verbosity** is now the dominant cost in `semantic_search`, `workspace_symbol` and `find_callers`:
  `signature` repeats the fully-qualified containing type that `containingType` also carries, and every location
  carries `endLine`/`endColumn`. Trimming it is a contract change across ~10 tools, so it wants evidence from
  VAL-001 (which response shapes were awkward) rather than a guess.
- **`analyze_symbol`** (884 tokens with all five sections) is not large enough to justify the capsule/budget shape
  the card floated; revisit if VAL-001 finds agents always calling it with everything on.
