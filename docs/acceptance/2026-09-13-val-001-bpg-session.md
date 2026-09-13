# VAL-001: two real sessions on BPG (2026-09-13)

Both sessions ran on the published exe from `a5cdf08`, with the global CLAUDE.md "reach for mcpRoslyn" section and the
bare-identifier Grep hook on. The per-call logs come from the transcripts; neither session was asked to keep one.

| run | session | task | prompt | blind? |
|---|---|---|---|---|
| 1 | `2bc4b839`, 07:08-07:36 | BPG **API-001**: ProblemDetails across all controllers (`cdea358`) | named mcpRoslyn and roslynk | **no** |
| 2 | `caf3a1d3`, 07:42-07:56 | BPG **DEAD-001**: delete the dead LLM path (`c1dad61`) | `hi`, then "hi could start with dead-001" | yes at prompt level, but see below |

## Verdict

- **Adoption, the question the card was built around.** In run 2 the agent used `find_references` for the reference
  questions that decided what to delete, and grep for sweeps across code and docs together. That is the reverse of the
  2026-08-15 finding that "grep is the reflex". Caveat: one session, and it worked from a card that named mcpRoslyn.
- **`find_dead_code_candidates` earns its keep.** It set run 2's scope (four more dead types deleted) and served as
  the check after deletion.
- **The Grep hook never fired in either run.** Every identifier grep was `\b`-wrapped or an alternation, and the hook
  only matches a bare single identifier.
- **Cold start doesn't hurt.** The first dead-code scan after a load takes ~2 s; everything else is tens of ms.
- **Bugs found:** DIAG-003 (carded), plus the entry-point false positive, the dead chain through a DI registration
  that the scan misses, and an empty `find_registrations` result that doesn't say whether the type exists.

## Run 2: blind re-run (DEAD-001)

**How blind it was.** The prompt was blind: the user's only steer was "hi could start with dead-001". The agent had
started on AUTO-002 and switched when that message arrived mid-turn. **The card was not blind:** run 1 wrote DEAD-001's
body, which says "Before deleting: confirm with mcpRoslyn `find_dead_code_candidates includePublicTypes:true`" and
cites `find_registrations`. So the session's first two mcpRoslyn calls were **following the card's instructions**. The
seven that followed were the agent's own choice.

**Call counts.**

| tool | calls |
|---|---:|
| Read / Bash / Edit / Grep / Write / Glob | 22 / 17 / 14 / 8 / 6 / 3 |
| **mcpRoslyn** (9 in all) | `find_references` 4, `find_dead_code_candidates` 2, `workspace_symbol` 1, `reload_workspace` 1, `find_registrations` 1 |
| Grep calls the hook denied | 0 |

**Per-call log** (mcpRoslyn calls, plus the greps and builds that competed with them).

| time | tool | asked | came back |
|---|---|---|---|
| 07:44:40 | `find_dead_code_candidates` *(card said to)* | `includePublicTypes: true` | 7 candidates, 4.1 s (first mcpRoslyn call of the session) |
| 07:44:41 | `find_registrations` *(card cites it)* | `"LLM"` | the same 10.8k chars as run 1. Returned 27 ms after the dead-code scan, i.e. it waited behind it. |
| 07:44:44 | Grep (count) | `\b(I?LLMService\|LlmResiliencePolicy\|CodeGenerationServiceV?2?\|MockLlmService\|Polly)\b`, whole repo | counts per file, docs included |
| 07:45:01 | Grep (content) | same pattern, src + tests + 4 docs | **174.6 KB**, saved to a file with only a preview shown; redone narrower at 07:45:35 (5.6k) |
| 07:46:03 | `workspace_symbol` | `ICodeGenerationService` | symbolId, 34 ms |
| 07:46:22 | `find_references` | `T:…ICodeGenerationService` | 2 refs, both in the dead classes, 25 ms |
| 07:46:32 | `find_references` ×3 | `GeneratedCode`, `GeneratedApplication`, `CodeType` (symbolIds typed directly, no lookup) | 8.3k / 1.5k / 4.8k chars, 23-49 ms. Keep `GeneratedCode` + `CodeType` (`XAFProjectService` uses them); delete `GeneratedApplication`. |
| 07:49:08 | Grep | `SpecToolCatalog\b`, files only | 2 files: a reference lookup, while editing docs |
| 07:49:18 | Bash `dotnet build` | after deletions | CS1061: removing Http.Polly dropped `Configuration.Binder`, which only ever arrived transitively |
| 07:50:28 | `reload_workspace` | (none) | 1.75 s |
| 07:50:41 | `find_dead_code_candidates` | same | only the entry-point false positive, 2.8 s (`dotnet test` running in the background). Used as the check that nothing new went dead. |
| 07:53:23 | Grep | leftover names, code + docs | doc mentions only |

**(1) Which tool call replaced several greps?** The four `find_references`, 23-49 ms each. They answered "does anything
outside the dead set use this type?" by file path. That decided how `ICodeGenerationService.cs` was split: two types
kept and moved to `GeneratedCode.cs`, the rest deleted. A text grep for `GeneratedCode` also matches longer names and
comments. The second dead-code scan replaced a leftover sweep.

**(2) Where was grep used when a tool would have been better, and why?** The multi-name sweeps at 07:44:44, 07:45:01,
07:45:35 and 07:53:23. For their docs and csproj half, grep was the right tool: `Polly` package references and
CLAUDE.md prose are text, and mcpRoslyn doesn't read `.md` files. What made grep cheaper was one pattern for 5-9 names
across code and docs, instead of one `find_references` per symbol plus a separate doc search. What it cost: one
174.6 KB result that had to be redone. The only pure symbol lookup via grep was `SpecToolCatalog\b`, small and
harmless.

**(3) Which response shape was awkward?** `find_references` on `GeneratedCode`: 8.3k chars for ~30 references, each
carrying `endLine`/`endColumn`, when the agent only needed which files. A per-file summary would have been enough. This
is the first concrete instance behind PERF-002's deferred location trim, and still just one. On the plus side, the agent
typed `T:BPG.Core.Interfaces.GeneratedCode` without a lookup and it worked: the doc-comment id format is guessable.

**(4) Did cold start hurt?** No. The first dead-code scan after a workspace load costs ~2 s: 2069 ms uncontended
(measured from the mcpRoslyn session at 07:39), and 4.1 s / 2.8 s in run 2 with other work running. Everything else was
23-49 ms. Two mcpRoslyn calls sent in one message ran one after the other: `find_registrations` (normally ~10 ms) waited
out the scan.

**(5) Which server did the agent use for which kind of task?** N/A: roslynk had been removed. Edits went through
Edit / Write / `git rm`.

## Run 1: primed (API-001)

**How it was primed.** The opening prompt said "let mcpRoslyn do some testing" and that the work "exercises the
orientation tools and roslynk's editing tools". The rationale in the mcpRoslyn session's handoff was pasted along with
the prompt, so every tool use below was primed. roslynk was registered in BPG's `.mcp.json` and removed at the end of
the session.

**Call counts.**

| tool | calls |
|---|---:|
| Read / Grep / Bash / Edit / Write | 28 / 26 / 17 / 9 / 4 |
| **mcpRoslyn** (7 in all) | `get_compilation_errors` 4, `find_registrations` 2, `reload_workspace` 1 |
| **roslynk** (17 in all) | `apply_patch` 10, `get_diagnostics` 2, `open_solution`, `get_solution_status`, `search_symbols`, `find_references`, `get_members` |
| Grep calls the hook denied | 0 |

**Per-call log.**

| time | tool | asked | came back |
|---|---|---|---|
| 07:09:09 | Grep | `ProblemDetails\|UseExceptionHandler\|...` in src | `GlobalExceptionMiddleware` already exists. Text search; grep was right. |
| 07:09:19 | Grep | controller error returns | ~40 returns in 5 shapes. Pattern search; grep was right. |
| 07:09:42 | **mcpRoslyn `find_registrations`** | `"LLM"` | 10.8k chars, 40 ms. `ILLMService → LLMService`, consumed only by `CodeGenerationService`/`V2`. This is the dead-Polly-path finding. |
| 07:09:55 | **mcpRoslyn `find_registrations`** | `"CodeGenerationService"` | empty, 13 ms. Doesn't say whether the type exists. |
| 07:10:07 | Grep | `\bILLMService\b` | consumers, i.e. a reference lookup |
| 07:10:12 | Grep | `\b(CodeGenerationService\|CodeGenerationServiceV2)\b` | never constructed, i.e. a reference lookup |
| 07:17:52-07:19:31 | roslynk `apply_patch` ×10 | 7 `.cs`, 3 `.tsx` | all applied |
| 07:20:01 | roslynk `get_diagnostics` | analyzers off | **13 × CS8618 on `BPGDbContext`, all false.** `dotnet build`: 0 errors. |
| 07:20:02 | **mcpRoslyn `get_compilation_errors`** | BPG.Api | 0, plus a stale-workspace warning naming the new test file. Correct. |
| 07:20:30 | **mcpRoslyn `reload_workspace`** | (none) | server `durationMs` 1917. The transcript shows 195 s, because the call waited behind a `dotnet build` + `dotnet test` Bash call. |
| 07:24:13 | **mcpRoslyn `get_compilation_errors`** | BPG.Api.IntegrationTests | 0 |
| 07:34:14 | **mcpRoslyn `get_compilation_errors`** | BPG.Data | **the same 13 false CS8618** (189 ms); with `includeAnalyzers: true`, 0 (660 ms). Filed as **DIAG-003**. |

**Findings that still stand.**
- `find_registrations "LLM"` exposed the dead chain in one call.
- The empty `find_registrations "CodeGenerationService"` sent the agent to grep (question 2), because it can't tell
  "exists, not registered" from "no such type".
- Both servers' fast compile checks are wrong on EF Core DbContexts (DIAG-003).
- roslynk took every edit, `.tsx` included, but the prompt had told it to use roslynk for editing.

**Timing caveat for anyone reconstructing from a transcript.** A transcript latency is the time between `tool_use` and
`tool_result`, so it includes waiting behind other calls. Use the server's own `durationMs`, or uncontended calls only.

## `find_dead_code_candidates` (TOOL-006)

On BPG before DEAD-001 it returned 7 candidates:

- **6 were real dead code, all since deleted in `c1dad61`:** `CodeGenerationService`, `CodeGenerationServiceV2`,
  `BPGDbInitializer`, `AnalysisResult`, `AnalysisProgress`, and a duplicate `NoOpConversationNotifier`.
- **1 false positive, reported at high confidence:** `<top-level-statements-entry-point>` in `Program.cs`. Run 2's agent
  was already calling it "a known false positive".
- **Missed:** `LLMService` and `LlmResiliencePolicy`. `LLMService` is DI-registered, so it is suppressed, even though
  its only consumers were the dead `CodeGenerationService` types. The policy was referenced only by `LLMService`.

The 2026-08-15 verdict that it didn't earn its keep is stale.

## What this means for the cards gated on VAL-001

- **TOOL-004** (`find_entrypoints` de-dup flag): never called in either run. No evidence.
- **TOOL-005** (wider `semantic_search` grammar): never called in either run. This is the third real session (with
  2026-08-15) where nothing wanted a missing pattern.
- **TOOL-007** (semantic diff): run 2 read `git diff --cached --stat` and relied on build, tests and the dead-code
  rescan. The card's own gate, "if VAL-001 shows the agent just reads the git diff, close this unbuilt", is met.
- **Not yet carded:**
  - the entry-point false positive
  - the dead chain through a DI registration that the scan misses
  - the empty `find_registrations` result that doesn't say whether the type exists
  - the Grep hook never firing
  - (weak, one instance) a per-file summary option for `find_references`
