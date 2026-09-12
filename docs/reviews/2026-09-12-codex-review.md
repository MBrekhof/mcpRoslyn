# Codex review — 2026-09-12

Read-only whole-project review by Codex, reproduced verbatim below.
Every finding was then checked against the source before carding; all 24 held. Carded as WS-006,
IDX-002, IDX-003, IDX-004, IDX-005, TOOL-008, TOOL-009, TOOL-010, TOOL-011, DIAG-002 — see TODO.md,
"From the Codex review (2026-09-12)", for the finding → card mapping and the severity adjustments.

---

**Verdict: changes needed.** The highest risks are lost edits during rename and silently incomplete or stale semantic results.

This was a read-only source review. No files changed; no builds or tests were run because they write artifacts and fixture files.

1. **Severity:** high
   **Location:** [RenameSymbolTool.cs:90](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Tools/RenameSymbolTool.cs:90)
   **Failure scenario:** An editor saves a file after `GetFreshSolutionAsync` captures it but before rename finishes. Rename then overwrites the entire file with text derived from the older snapshot, silently discarding the editor's changes. Two concurrent renames can similarly overwrite each other.
   **Suggested fix:** Serialize server-side write operations and verify each file still matches its original snapshot before committing edits. Reject stale edits explicitly.

2. **Severity:** high
   **Location:** [RenameSymbolTool.cs:84](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Tools/RenameSymbolTool.cs:84)
   **Failure scenario:** A rename affects several files. The first write succeeds, but a subsequent file is read-only or the request is cancelled. Earlier writes remain applied while the tool returns an error or cancellation, leaving a partially renamed solution without recovery information.
   **Suggested fix:** Stage and validate all changes before writing, preserve original bytes, and implement rollback or explicit partial-application reporting. Preserve source encodings when writing.

3. **Severity:** high
   **Location:** [WorkspaceService.cs:221](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/WorkspaceService.cs:221)
   **Failure scenario:** Reload occurs while the previous warm-up is compiling. Reload replaces `_symbolIndex` and `_invocationIndex`, but the old warm-up subsequently reads those mutable fields and builds the new indexes from the old solution. Both generations can append to the same indexes, producing stale symbols and duplicate invocation entries.
   **Suggested fix:** Capture the solution and its specific index instances together as one generation. Cancel and observe obsolete warm-ups; publish and query matching solution/index generations.

4. **Severity:** high
   **Location:** [WorkspaceService.cs:183](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/WorkspaceService.cs:183)
   **Failure scenario:** Immediately after startup or reload, an indexed tool returns empty or partially populated results as success. Production callers neither await readiness nor fall back to a complete live query. An index-build exception can leave this condition indefinitely. `tests/mcpRoslyn.Tests/TestHelpers/TestHost.cs:60` explicitly waits away this race in tests.
   **Suggested fix:** Track index readiness/failure and await it, provide a complete fallback, or return an explicit unavailable result. Add tests that query before warm-up completes. Correct the acceptance doc's claim that callers await warm-up (`docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md:46`).

5. **Severity:** medium
   **Location:** [WorkspaceService.cs:147](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/WorkspaceService.cs:147)
   **Failure scenario:** Reload replaces the indexes and workspace, then `OpenSolutionAsync` throws or is cancelled. `_solution` retains the previous solution, but its indexes have been replaced with empty ones and no new warm-up starts. Subsequent tools operate on inconsistent state.
   **Suggested fix:** Construct replacement state locally and publish it only after successful loading. On failure, retain the complete previous generation or mark the service unavailable.

6. **Severity:** medium
   **Location:** [WorkspaceService.cs:151](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/WorkspaceService.cs:151)
   **Failure scenario:** Every reload creates another `MSBuildWorkspace`. The previous instance receives `CloseSolution()` but is never disposed; shutdown disposes only the latest instance. Disposal also neither cancels nor awaits outstanding warm-up work.
   **Suggested fix:** Give each generation explicit ownership and retirement: cancel/await its background work, then dispose its workspace once outstanding readers finish.

7. **Severity:** medium
   **Location:** [InvocationIndex.cs:158](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/InvocationIndex.cs:158)
   **Failure scenario:** Query A clears the dirty set and removes a document's entries, then pauses while obtaining its semantic model. Query B sees no dirty work and returns the temporarily incomplete index. If A throws, those entries stay missing because the dirty marker was already discarded. Concurrent refreshes/builds can also append overlapping results.
   **Suggested fix:** Build replacement document entries outside the shared collections, then atomically publish them against a captured solution version. Clear dirty markers only after successful publication.

8. **Severity:** medium
   **Location:** [InvocationIndex.cs:168](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/InvocationIndex.cs:168)
   **Failure scenario:** Touching the fixture's `PollingWorker.cs`, even with a comment-only change, removes its `BackgroundService` subclass entry. `RefreshDirty` calls only `IndexDocument`, while subclass detection exists exclusively in `BuildAsync`. The service disappears until reload.
   **Suggested fix:** Include subclass discovery in the document replacement path. Test unchanged, newly added, and removed subclasses after refresh.

9. **Severity:** medium
   **Location:** [WorkspaceService.cs:74](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/WorkspaceService.cs:74)
   **Failure scenario:** `Aliases.cs` changes `global using Result = System.Int32;` to `System.String`, while an unchanged document declares `Result Get()`. Only `Aliases.cs` becomes dirty, so the indexed method remains under `returns:int` instead of moving to `returns:string`. Semantic dependencies cross document boundaries.
   **Suggested fix:** Invalidate affected projects and dependent projects for declaration/global-binding changes, or version semantic indexes by compilation rather than only by changed document.

10. **Severity:** medium
    **Location:** [WorkspaceService.cs:66](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/WorkspaceService.cs:66)
    **Failure scenario:** Restoring a different file version with an older timestamp is ignored because `cachedMtime >= diskMtime` treats it as unchanged. A future-dated timestamp can suppress subsequent ordinary edits until wall-clock time catches up.
    **Suggested fix:** Detect timestamp inequality rather than only increases. Use additional content/version checks where timestamp-preserving replacements must be supported.

11. **Severity:** medium
    **Location:** [ToolError.cs:9](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Contracts/ToolError.cs:9)
    **Failure scenario:** A missing symbol returns `ToolResult<T>.Fail`, but this is an ordinary return object. The pinned SDK serializes it without setting MCP `isError`; clients checking the protocol error flag treat the failed invocation as successful (per SDK v1.3.0's `AIFunctionMcpServerTool.cs` result conversion).
    **Suggested fix:** Translate failures into `CallToolResult` with `IsError = true`, retaining the structured error payload. Add transport-level tests; direct `InvokeAsync` tests cannot verify this.

12. **Severity:** medium
    **Location:** [RoslynHelpers.cs:25](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Tools/RoslynHelpers.cs:25)
    **Failure scenario:** An oversized column is added to the line start without checking the line's length. It can land on an identifier on a later line, causing navigation—or rename—to target the wrong symbol. Invalid line numbers instead become `INTERNAL_ERROR` through the generic exception handler.
    **Suggested fix:** Validate one-based line and column bounds before calculating the offset, and return `POSITION_INVALID`.

13. **Severity:** medium
    **Location:** [SymbolIndex.cs:164](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/SymbolIndex.cs:164)
    **Failure scenario:** Two independent projects declare `Acme.Options`. Both receive declaration ID `T:Acme.Options`, so index deduplication removes one. `ResolveSymbolByIdAsync` (`src/mcpRoslyn/Tools/RoslynHelpers.cs:43`) likewise returns the first match, making the second symbol inaccessible by ID and potentially resolving a cached entry against the wrong project.
    **Suggested fix:** Include project/assembly identity in internal keys and public symbol addressing. Report ambiguity when a legacy declaration ID resolves to multiple distinct symbols.

14. **Severity:** medium
    **Location:** [InvocationIndex.cs:246](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/InvocationIndex.cs:246)
    **Failure scenario:** An ordinary application method named `Map` or `MapGet` is reported as an ASP.NET endpoint. `TryBuildRoute` accepts a semantic model but never uses it to verify the method or receiver. `TryBuildHostedService` similarly accepts unrelated generic methods named `AddHostedService`.
    **Suggested fix:** Verify the resolved method and relevant receiver/parameter types before classifying these calls. Add negative fixtures containing unrelated methods with those names.

15. **Severity:** medium
    **Location:** [InvocationIndex.cs:262](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/InvocationIndex.cs:262)
    **Failure scenario:** `app.MapMethods("/health", new[] { "GET", "HEAD" }, Handler)` is reported with verb `ANY` and the HTTP-method array as its handler. The parser assumes the second argument is always the handler.
    **Suggested fix:** Map arguments using the resolved overload's parameters, including named arguments, and represent the actual allowed methods.

16. **Severity:** medium
    **Location:** [AnalyzeSymbolTool.cs:151](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Tools/AnalyzeSymbolTool.cs:151)
    **Failure scenario:** Analyzing `M:TestLib.IGreeter.Greet(System.String)` returns `Implementations = null` despite the fixture's two implementations. The composite rejects every non-type symbol, whereas `find_implementations` passes interface members to Roslyn.
    **Suggested fix:** Support applicable method/property/event symbols and share implementation-query behavior with the standalone tool.

17. **Severity:** medium
    **Location:** [GetCompilationErrorsTool.cs:31](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Tools/GetCompilationErrorsTool.cs:31)
    **Failure scenario:** A solution references a missing project. `get_compilation_errors` checks only successfully loaded projects and ignores workspace failures, so it can return "0 errors, 0 warnings." This contradicts its advertised "would dotnet build succeed?" behavior. An unknown `projectName` also silently produces an empty success.
    **Suggested fix:** Surface load failures/incomplete coverage, validate project filters, and describe the result as compiler diagnostics for loaded projects rather than a build-success verdict.

18. **Severity:** medium
    **Location:** [SymbolIndex.cs:167](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/SymbolIndex.cs:167)
    **Failure scenario:** After editing many documents, every indexed query permanently re-walks all of them: `_dirty` is never cleared and refreshed results are never cached. DI consumer lookup repeats these walks per registration, including another lookup for its simple name. Query cost grows with the session's entire edit history.
    **Suggested fix:** Cache successfully rebuilt entries by semantic version and clear corresponding dirty markers safely. Batch consumer lookups and use asynchronous, cancellation-aware refreshes.

19. **Severity:** medium
    **Location:** [InvocationIndex.cs:76](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/InvocationIndex.cs:76)
    **Failure scenario:** Every project walks types from `compilation.GlobalNamespace`, including referenced assemblies, to find hosted-service subclasses. Metadata-only matches are discarded only after walking inheritance — the same broad traversal already removed from `SymbolIndex`.
    **Suggested fix:** Walk `compilation.Assembly.GlobalNamespace` for source declarations, or incorporate subclass detection into document indexing. Verify result equivalence and measure the change.

20. **Severity:** medium
    **Location:** [SolutionDiscovery.cs:43](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Workspace/SolutionDiscovery.cs:43)
    **Failure scenario:** During downward discovery, an inaccessible directory sorts before another directory containing the solution. `SolutionsIn(dir)` calls `GetFiles` outside the exception handler, so discovery aborts instead of skipping the inaccessible directory. A directory removed after enqueueing has the same problem.
    **Suggested fix:** Apply inaccessible/disappearing-directory handling to file enumeration as well as child-directory enumeration, including the upward search.

21. **Severity:** low
    **Location:** [GetCompilationErrorsTool.cs:85](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Tools/GetCompilationErrorsTool.cs:85)
    **Failure scenario:** A caller supplies `excludeDiagnosticSources`, which both diagnostic tools advertise, but the implementation explicitly discards it. The response gives no indication that the requested filter was ignored.
    **Suggested fix:** Implement a meaningful source field/filter, or remove the advertised parameter and reject nonempty values until supported. Correct the corresponding architecture documentation.

22. **Severity:** low
    **Location:** [GetCompilationErrorsTool.cs:52](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Tools/GetCompilationErrorsTool.cs:52)
    **Failure scenario:** Requesting exact `severity: "Info"` or `"Hidden"` still applies the default `minimumSeverity: "Warning"`, necessarily filtering out every matching diagnostic. The document tool has the same behavior.
    **Suggested fix:** Define precedence between exact severity and minimum severity; an explicit exact severity should override the default threshold, or incompatible inputs should be rejected.

23. **Severity:** low
    **Location:** [ListDocumentSymbolsTool.cs:43](/C:/Projects/mcpRoslyn/src/mcpRoslyn/Tools/ListDocumentSymbolsTool.cs:43)
    **Failure scenario:** A document containing delegates, indexers, and enum members returns an incomplete outline because none of those declaration syntax kinds pass the filter.
    **Suggested fix:** Include those declaration kinds and add a fixture asserting their presence.

24. **Severity:** low
    **Location:** [WorkspaceServiceTests.cs:25](/C:/Projects/mcpRoslyn/tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs:25)
    **Failure scenario:** Workspace and symbol-index tests repeatedly construct services without disposing them; some return while warm-up is still running. The test host also discards its built `ServiceProvider` without disposal. Repeated runs skip resource cleanup and permit background work to outlive its test.
    **Suggested fix:** Use `await using` consistently, retain/dispose the test service provider, and ensure each test's background work is cancelled or completed before teardown.
