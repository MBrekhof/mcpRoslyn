# mcpRoslyn Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a Windows-only .NET 10 MCP server that exposes 12 Roslyn-backed code-intelligence tools over stdio for AI coding agents.

**Architecture:** Single C# console exe. `ModelContextProtocol` SDK (Microsoft official) hosts stdio. `Microsoft.CodeAnalysis.Workspaces.MSBuild` loads a solution once at startup, kept in a DI-singleton `WorkspaceService`. Each tool call refreshes changed source files via mtime comparison before querying Roslyn's semantic model. Source-only writes happen exclusively when `rename_symbol` is called with `applyEdits: true`.

**Tech Stack:** .NET 10, C#, `ModelContextProtocol`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`, `Microsoft.Build.Locator`, `Microsoft.Extensions.Hosting`, NUnit, FluentAssertions.

**Design reference:** [`2026-05-15-mcproslyn-design.md`](./2026-05-15-mcproslyn-design.md)

**Working directory:** `c:\projects\mcpRoslyn\`

---

## Conventions for every task

- **Branch:** all work on `main` (this is a fresh repo, no branching needed for v1).
- **Test framework:** NUnit + FluentAssertions. Match duetGPT convention.
- **Build command:** `dotnet build mcpRoslyn.sln -c Release` — must produce 0 errors before any commit.
- **Test command:** `dotnet test mcpRoslyn.sln --filter "FullyQualifiedName~<Class>"` — targeted by class to avoid running the whole suite for one task.
- **Commit style:** Conventional commits (`feat:`, `test:`, `chore:`, `docs:`). One commit per task. Co-author trailer: `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.
- **stdout discipline:** never `Console.WriteLine` anywhere outside the MCP SDK's own writes. All diagnostic output goes through `ILogger` wired to `Console.Error`.
- **Mtime / Roslyn quirk:** `MSBuildWorkspace.OpenSolutionAsync` must run AFTER `MSBuildLocator.RegisterDefaults()`. If you skip the locator, MSBuild assembly resolution fails at runtime with cryptic `FileNotFoundException` on `Microsoft.Build`. Confirm `MSBuildLocator` is registered in `Program.cs` before anything else.

---

## Phase 1 — Foundation

### Task 1: Scaffold solution + projects

**Files:**
- Create: `c:\projects\mcpRoslyn\mcpRoslyn.sln`
- Create: `c:\projects\mcpRoslyn\src\mcpRoslyn\mcpRoslyn.csproj`
- Create: `c:\projects\mcpRoslyn\src\mcpRoslyn\Program.cs` (stub)
- Create: `c:\projects\mcpRoslyn\tests\mcpRoslyn.Tests\mcpRoslyn.Tests.csproj`

**Step 1: Invoke `csharp-mcp-server-generator` skill**

Use the skill to scaffold the src project. Direct it to:
- output path: `c:\projects\mcpRoslyn\src\mcpRoslyn\`
- project name: `mcpRoslyn`
- target framework: `net10.0`
- transport: stdio
- include a single placeholder `EchoTool` so we know the SDK wiring is alive.

If the skill output deviates from the design's `Tools\` layout, accept the generator's csproj/Program.cs but discard any sample tool — we'll replace it.

**Step 2: Create the .sln file**

Run: `dotnet new sln -n mcpRoslyn -o c:\projects\mcpRoslyn`

**Step 3: Create the test project**

Run: `dotnet new nunit -n mcpRoslyn.Tests -o c:\projects\mcpRoslyn\tests\mcpRoslyn.Tests -f net10.0`

**Step 4: Add both projects to the .sln**

Run:
```
dotnet sln c:\projects\mcpRoslyn\mcpRoslyn.sln add c:\projects\mcpRoslyn\src\mcpRoslyn\mcpRoslyn.csproj
dotnet sln c:\projects\mcpRoslyn\mcpRoslyn.sln add c:\projects\mcpRoslyn\tests\mcpRoslyn.Tests\mcpRoslyn.Tests.csproj
```

**Step 5: Add test → src project reference + NuGet packages**

In `tests\mcpRoslyn.Tests\mcpRoslyn.Tests.csproj` add:
```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\mcpRoslyn\mcpRoslyn.csproj" />
</ItemGroup>
<ItemGroup>
  <PackageReference Include="FluentAssertions" Version="6.12.0" />
</ItemGroup>
```

In `src\mcpRoslyn\mcpRoslyn.csproj` confirm/add:
```xml
<PackageReference Include="ModelContextProtocol" Version="*" />
<PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="*" />
<PackageReference Include="Microsoft.Build.Locator" Version="*" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="*" />
```

(Pin to current-latest at install time — record exact versions in the commit.)

**Step 6: Verify build**

Run: `dotnet build c:\projects\mcpRoslyn\mcpRoslyn.sln -c Release`
Expected: 0 errors, 0 warnings.

**Step 7: Commit**

```
cd c:\projects\mcpRoslyn
git add .
git commit -m "chore: scaffold sln, src, and tests projects"
```

---

### Task 2: Wire `MSBuildLocator` + DI host

**Files:**
- Modify: `src\mcpRoslyn\Program.cs`
- Create: `src\mcpRoslyn\Options\McpRoslynOptions.cs`

**Step 1: Write `McpRoslynOptions.cs`**

```csharp
namespace McpRoslyn.Options;

public sealed class McpRoslynOptions
{
    public required string SolutionPath { get; init; }
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
}
```

**Step 2: Replace `Program.cs`**

```csharp
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using McpRoslyn.Options;

// MUST be first - before any Microsoft.CodeAnalysis.* type is touched.
MSBuildLocator.RegisterDefaults();

var options = ParseArgs(args);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(options.LogLevel);

builder.Services.AddSingleton(options);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

static McpRoslynOptions ParseArgs(string[] args)
{
    string? solution = null;
    var logLevel = LogLevel.Information;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--solution" when i + 1 < args.Length:
                solution = args[++i];
                break;
            case "--log-level" when i + 1 < args.Length:
                if (Enum.TryParse<LogLevel>(args[++i], true, out var level))
                    logLevel = level;
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(solution))
        throw new ArgumentException("--solution <path> is required");
    if (!File.Exists(solution))
        throw new FileNotFoundException($"Solution file not found: {solution}");

    return new McpRoslynOptions { SolutionPath = solution, LogLevel = logLevel };
}
```

**Step 3: Build + run sanity check**

Build: `dotnet build c:\projects\mcpRoslyn\mcpRoslyn.sln -c Release`
Expected: 0 errors.

Run: `dotnet run --project src/mcpRoslyn -- --solution c:\projects\mcpRoslyn\mcpRoslyn.sln`
Expected: process starts, blocks on stdin (waiting for MCP JSON-RPC). Kill with Ctrl+C. No stdout output; any logs land on stderr.

**Step 4: Commit**

```
git add src/mcpRoslyn/Program.cs src/mcpRoslyn/Options/
git commit -m "feat: wire MSBuildLocator and DI host with stdio MCP transport"
```

---

### Task 3: Build the fixture test solution

**Files:**
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestSolution.sln`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestLib/TestLib.csproj`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestApp/TestApp.csproj`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestLib/IGreeter.cs`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestLib/EnglishGreeter.cs`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestLib/DutchGreeter.cs`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestLib/Shape.cs` (abstract + 2 derived in same file)
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestLib/Partial1.cs`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestLib/Partial2.cs`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestLib/MyAttribute.cs`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestApp/Program.cs`
- Create: `tests/mcpRoslyn.Tests/Fixtures/TestSolution/TestApp/BrokenClass.cs` (compile error)

**Step 1: Solution + csproj files**

`TestLib.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

`TestApp.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\TestLib\TestLib.csproj" />
  </ItemGroup>
</Project>
```

Create `TestSolution.sln` with `dotnet sln` commands (in the fixture dir).

**Step 2: Source files** — write the 8 .cs files with deliberate, asserted-against contents:

`TestLib/IGreeter.cs`:
```csharp
namespace TestLib;

public interface IGreeter
{
    string Greet(string name);
}
```

`TestLib/EnglishGreeter.cs`:
```csharp
namespace TestLib;

public class EnglishGreeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}!";
}
```

`TestLib/DutchGreeter.cs`:
```csharp
namespace TestLib;

public class DutchGreeter : IGreeter
{
    public string Greet(string name) => $"Hallo, {name}!";
}
```

`TestLib/Shape.cs`:
```csharp
namespace TestLib;

public abstract class Shape
{
    public abstract double Area();
}

public class Circle(double radius) : Shape
{
    public override double Area() => Math.PI * radius * radius;
}

public class Square(double side) : Shape
{
    public override double Area() => side * side;
}
```

`TestLib/Partial1.cs`:
```csharp
namespace TestLib;

public partial class PartialThing
{
    public int Foo() => 1;
}
```

`TestLib/Partial2.cs`:
```csharp
namespace TestLib;

public partial class PartialThing
{
    public int Bar() => 2;
}
```

`TestLib/MyAttribute.cs`:
```csharp
using System;

namespace TestLib;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class MyMarkerAttribute : Attribute { }

[MyMarker]
public class MarkedType
{
    [MyMarker]
    public void MarkedMethod() { }
}
```

`TestApp/Program.cs`:
```csharp
using TestLib;

IGreeter greeter = new EnglishGreeter();
Console.WriteLine(greeter.Greet("World"));
```

`TestApp/BrokenClass.cs`:
```csharp
namespace TestApp;

public class BrokenClass
{
    public int Add(int a, int b) => a + ; // deliberate syntax error
}
```

**Step 3: Mark fixture files as Content (copied to test output)**

In `mcpRoslyn.Tests.csproj` add:
```xml
<ItemGroup>
  <None Include="Fixtures\TestSolution\**\*.*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <None Remove="**\bin\**" />
    <None Remove="**\obj\**" />
  </None>
</ItemGroup>
```

Tests will load the fixture solution from `AppContext.BaseDirectory`. Do NOT let MSBuild try to build the fixture during the test build — exclude with `<None Include>` (above) instead of `<Compile>`.

**Step 4: Verify test project still builds**

Run: `dotnet build c:\projects\mcpRoslyn\mcpRoslyn.sln -c Release`
Expected: 0 errors.

**Step 5: Commit**

```
git add tests/mcpRoslyn.Tests/Fixtures/ tests/mcpRoslyn.Tests/mcpRoslyn.Tests.csproj
git commit -m "test: add fixture solution for tool tests"
```

---

### Task 4: Contracts (DTOs)

**Files:**
- Create: `src/mcpRoslyn/Contracts/SymbolLocation.cs`
- Create: `src/mcpRoslyn/Contracts/SymbolInfo.cs`
- Create: `src/mcpRoslyn/Contracts/DiagnosticInfo.cs`
- Create: `src/mcpRoslyn/Contracts/ToolError.cs`

**Step 1: Write the DTOs**

`SymbolLocation.cs`:
```csharp
namespace McpRoslyn.Contracts;

public sealed record SymbolLocation(
    string FilePath,
    int Line,           // 1-based
    int Column,         // 1-based
    int EndLine,
    int EndColumn,
    string? Snippet = null);
```

`SymbolInfo.cs`:
```csharp
namespace McpRoslyn.Contracts;

public sealed record SymbolInfo(
    string Name,
    string Kind,                  // "Class", "Method", "Property", etc.
    string SymbolId,              // DocumentationCommentId
    string? ContainingType,
    string Accessibility,
    string Signature,
    SymbolLocation? PrimaryLocation = null);
```

`DiagnosticInfo.cs`:
```csharp
namespace McpRoslyn.Contracts;

public sealed record DiagnosticInfo(
    string Severity,   // "Error", "Warning", "Info", "Hidden"
    string Code,       // e.g. "CS1002"
    string Message,
    SymbolLocation Location);
```

`ToolError.cs`:
```csharp
namespace McpRoslyn.Contracts;

public sealed record ToolError(string Code, string Message, string? Hint = null);

public sealed record ToolResult<T>(T? Result = null, ToolError? Error = null) where T : class
{
    public static ToolResult<T> Ok(T value) => new(Result: value);
    public static ToolResult<T> Fail(string code, string message, string? hint = null)
        => new(Error: new ToolError(code, message, hint));
}
```

**Step 2: Build**

Run: `dotnet build c:\projects\mcpRoslyn\mcpRoslyn.sln -c Release`
Expected: 0 errors.

**Step 3: Commit**

```
git add src/mcpRoslyn/Contracts/
git commit -m "feat: contracts — SymbolLocation, SymbolInfo, DiagnosticInfo, ToolError"
```

---

### Task 5: `IWorkspaceService` + skeleton `WorkspaceService`

**Files:**
- Create: `src/mcpRoslyn/Workspace/IWorkspaceService.cs`
- Create: `src/mcpRoslyn/Workspace/WorkspaceService.cs`
- Modify: `src/mcpRoslyn/Program.cs` (register as singleton + eager load)

**Step 1: `IWorkspaceService.cs`**

```csharp
using Microsoft.CodeAnalysis;

namespace McpRoslyn.Workspace;

public interface IWorkspaceService
{
    Task LoadAsync(CancellationToken ct = default);
    Task ReloadAsync(CancellationToken ct = default);
    Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default);
    int LoadedProjectCount { get; }
}
```

**Step 2: `WorkspaceService.cs` skeleton (load only — mtime refresh comes in Task 7)**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using McpRoslyn.Options;

namespace McpRoslyn.Workspace;

public sealed class WorkspaceService(McpRoslynOptions options, ILogger<WorkspaceService> log)
    : IWorkspaceService, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly Dictionary<DocumentId, DateTime> _mtimeCache = new();

    public int LoadedProjectCount => _solution?.Projects.Count() ?? 0;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await LoadUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _workspace?.CloseSolution();
            _mtimeCache.Clear();
            await LoadUnsafeAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_solution is null) throw new InvalidOperationException("Workspace not loaded.");
            // mtime refresh added in Task 7
            return _solution;
        }
        finally { _gate.Release(); }
    }

    private async Task LoadUnsafeAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _workspace = MSBuildWorkspace.Create();
        _workspace.WorkspaceFailed += (_, e) =>
            log.LogWarning("MSBuild workspace event: {Kind} {Message}", e.Diagnostic.Kind, e.Diagnostic.Message);

        _solution = await _workspace.OpenSolutionAsync(options.SolutionPath, cancellationToken: ct);
        log.LogInformation("Loaded {ProjectCount} projects in {Elapsed} ms from {Path}",
            _solution.Projects.Count(), sw.ElapsedMilliseconds, options.SolutionPath);

        // seed mtime cache
        foreach (var doc in _solution.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
            _mtimeCache[doc.Id] = File.GetLastWriteTimeUtc(doc.FilePath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _workspace?.Dispose(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
```

**Step 3: Wire into DI in `Program.cs`**

Add before `AddMcpServer()`:
```csharp
builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();
builder.Services.AddHostedService<WorkspaceLoaderHostedService>();
```

Add a tiny hosted service to trigger eager load at startup:
```csharp
// Program.cs — add at bottom of file
internal sealed class WorkspaceLoaderHostedService(IWorkspaceService ws) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => ws.LoadAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

**Step 4: Build**

Run: `dotnet build c:\projects\mcpRoslyn\mcpRoslyn.sln -c Release`
Expected: 0 errors.

**Step 5: Commit**

```
git add src/mcpRoslyn/Workspace/ src/mcpRoslyn/Program.cs
git commit -m "feat: WorkspaceService skeleton with eager load via hosted service"
```

---

### Task 6: Test — `WorkspaceService.LoadAsync` loads fixture solution

**Files:**
- Create: `tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs`
- Create: `tests/mcpRoslyn.Tests/TestHelpers/FixturePaths.cs`

**Step 1: Write `FixturePaths.cs` helper**

```csharp
namespace McpRoslyn.Tests.TestHelpers;

public static class FixturePaths
{
    public static string TestSolutionPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "TestSolution", "TestSolution.sln");
}
```

**Step 2: Write the failing test**

```csharp
using FluentAssertions;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using McpRoslyn.Options;
using McpRoslyn.Tests.TestHelpers;
using McpRoslyn.Workspace;
using NUnit.Framework;

namespace McpRoslyn.Tests;

[TestFixture]
public class WorkspaceServiceTests
{
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    [Test]
    public async Task LoadAsync_loads_fixture_solution_and_finds_both_projects()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();

        sut.LoadedProjectCount.Should().Be(2);
    }
}
```

**Step 3: Run test to verify it passes**

Run: `dotnet test mcpRoslyn.sln --filter "FullyQualifiedName~WorkspaceServiceTests" -c Release`
Expected: 1 test, PASS. If FAIL with `FileNotFoundException` on the fixture solution, the `<None Include>` from Task 3 didn't copy — fix that csproj entry.

**Step 4: Commit**

```
git add tests/mcpRoslyn.Tests/
git commit -m "test: WorkspaceService loads fixture solution"
```

---

### Task 7: Mtime-based refresh in `GetFreshSolutionAsync`

**Files:**
- Modify: `src/mcpRoslyn/Workspace/WorkspaceService.cs`
- Modify: `tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs`

**Step 1: Write failing test — file change is picked up**

Add to `WorkspaceServiceTests.cs`:
```csharp
[Test]
public async Task GetFreshSolutionAsync_picks_up_file_changes_via_mtime()
{
    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
    await sut.LoadAsync();

    var solution = await sut.GetFreshSolutionAsync();
    var doc = solution.Projects
        .SelectMany(p => p.Documents)
        .First(d => d.Name == "EnglishGreeter.cs");
    var originalText = (await doc.GetTextAsync()).ToString();

    // mutate the file on disk
    var backup = File.ReadAllText(doc.FilePath!);
    try
    {
        File.WriteAllText(doc.FilePath!, originalText.Replace("Hello", "Hi"));
        File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddSeconds(1));

        var refreshed = await sut.GetFreshSolutionAsync();
        var refreshedDoc = refreshed.GetDocument(doc.Id)!;
        var refreshedText = (await refreshedDoc.GetTextAsync()).ToString();

        refreshedText.Should().Contain("Hi, ");
        refreshedText.Should().NotContain("Hello,");
    }
    finally
    {
        File.WriteAllText(doc.FilePath!, backup);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test mcpRoslyn.sln --filter "FullyQualifiedName~GetFreshSolutionAsync_picks_up" -c Release`
Expected: FAIL — text still contains "Hello" because the skeleton's `GetFreshSolutionAsync` returns the cached solution unchanged.

**Step 3: Implement the mtime walk**

Replace `GetFreshSolutionAsync` body in `WorkspaceService.cs`:
```csharp
public async Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default)
{
    await _gate.WaitAsync(ct);
    try
    {
        if (_solution is null) throw new InvalidOperationException("Workspace not loaded.");

        foreach (var doc in _solution.Projects.SelectMany(p => p.Documents).ToList())
        {
            if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
            var diskMtime = File.GetLastWriteTimeUtc(doc.FilePath);
            if (_mtimeCache.TryGetValue(doc.Id, out var cachedMtime) && cachedMtime >= diskMtime)
                continue;

            var text = await File.ReadAllTextAsync(doc.FilePath, ct);
            _solution = _solution.WithDocumentText(
                doc.Id,
                Microsoft.CodeAnalysis.Text.SourceText.From(text));
            _mtimeCache[doc.Id] = diskMtime;
        }

        return _solution;
    }
    finally { _gate.Release(); }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test mcpRoslyn.sln --filter "FullyQualifiedName~GetFreshSolutionAsync_picks_up" -c Release`
Expected: PASS. Also re-run the load test to make sure it still passes.

**Step 5: Commit**

```
git add src/mcpRoslyn/Workspace/WorkspaceService.cs tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs
git commit -m "feat: mtime-based per-call refresh in GetFreshSolutionAsync"
```

---

### Task 8: `ToolBase` + error envelope + Roslyn helpers

**Files:**
- Create: `src/mcpRoslyn/Tools/ToolBase.cs`
- Create: `src/mcpRoslyn/Tools/RoslynHelpers.cs`

**Step 1: `RoslynHelpers.cs`**

Static helpers that every tool needs: resolve `Document` from filePath, resolve `ISymbol` from cursor or symbolId, build `SymbolLocation`/`SymbolInfo`.

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using McpRoslyn.Contracts;

namespace McpRoslyn.Tools;

internal static class RoslynHelpers
{
    public static Document? FindDocument(Solution solution, string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        return solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d =>
                d.FilePath is not null &&
                string.Equals(Path.GetFullPath(d.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<ISymbol?> ResolveSymbolAtPositionAsync(
        Document document, int line, int column, CancellationToken ct)
    {
        var text = await document.GetTextAsync(ct);
        var position = text.Lines[line - 1].Start + (column - 1);
        var semantic = await document.GetSemanticModelAsync(ct);
        if (semantic is null) return null;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null) return null;
        var token = root.FindToken(position);
        var node = token.Parent;
        if (node is null) return null;

        var info = semantic.GetSymbolInfo(node, ct);
        return info.Symbol ?? info.CandidateSymbols.FirstOrDefault()
            ?? semantic.GetDeclaredSymbol(node, ct);
    }

    public static async Task<ISymbol?> ResolveSymbolByIdAsync(
        Solution solution, string symbolId, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(symbolId, compilation);
            if (symbols.Length > 0) return symbols[0];
        }
        return null;
    }

    public static SymbolLocation? ToLocation(Location loc)
    {
        if (!loc.IsInSource || loc.SourceTree?.FilePath is null) return null;
        var span = loc.GetLineSpan();
        return new SymbolLocation(
            FilePath: loc.SourceTree.FilePath,
            Line: span.StartLinePosition.Line + 1,
            Column: span.StartLinePosition.Character + 1,
            EndLine: span.EndLinePosition.Line + 1,
            EndColumn: span.EndLinePosition.Character + 1);
    }

    public static SymbolInfo ToSymbolInfo(ISymbol symbol)
        => new(
            Name: symbol.Name,
            Kind: symbol.Kind.ToString(),
            SymbolId: DocumentationCommentId.CreateDeclarationId(symbol) ?? "",
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            Accessibility: symbol.DeclaredAccessibility.ToString(),
            Signature: symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            PrimaryLocation: symbol.Locations.Select(ToLocation).FirstOrDefault(l => l is not null));
}
```

**Step 2: `ToolBase.cs`**

```csharp
using Microsoft.Extensions.Logging;
using McpRoslyn.Contracts;
using McpRoslyn.Workspace;

namespace McpRoslyn.Tools;

internal abstract class ToolBase(IWorkspaceService workspace, ILogger logger)
{
    protected IWorkspaceService Workspace => workspace;
    protected ILogger Log => logger;

    protected async Task<ToolResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<ToolResult<T>>> body,
        CancellationToken ct) where T : class
    {
        try { return await body(ct); }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException ex)
        {
            return ToolResult<T>.Fail("FILE_NOT_IN_WORKSPACE", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult<T>.Fail("WORKSPACE_NOT_LOADED", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool failure");
            return ToolResult<T>.Fail("INTERNAL_ERROR", ex.Message);
        }
    }
}
```

**Step 3: Build**

Run: `dotnet build c:\projects\mcpRoslyn\mcpRoslyn.sln -c Release`
Expected: 0 errors.

**Step 4: Commit**

```
git add src/mcpRoslyn/Tools/
git commit -m "feat: ToolBase error envelope and shared Roslyn helpers"
```

---

## Phase 2 — Tools (TDD, one per task)

Every tool task follows the same shape:

1. Write a focused test against the fixture solution. Assert exact expected results.
2. Run the test → confirm it fails.
3. Implement the tool class with `[McpServerToolType]` / `[McpServerTool]` attributes.
4. Run the test → confirm it passes.
5. Commit `feat: <tool_name>`.

**Standard tool class shape:**

```csharp
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using McpRoslyn.Contracts;
using McpRoslyn.Workspace;

namespace McpRoslyn.Tools;

[McpServerToolType]
internal sealed class XxxTool(IWorkspaceService ws, ILogger<XxxTool> log) : ToolBase(ws, log)
{
    [McpServerTool(Name = "xxx_tool_name")]
    [Description("...")]
    public Task<ToolResult<ResultDto>> InvokeAsync(
        string filePath, int line, int column,
        CancellationToken ct)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            // tool-specific logic
            return ToolResult<ResultDto>.Ok(new ResultDto(...));
        }, ct);
}
```

### Task 9: `ReloadWorkspaceTool`

Simplest tool — calls `IWorkspaceService.ReloadAsync()`. Use this to validate that the SDK actually picks up `[McpServerToolType]` classes via `WithToolsFromAssembly()`.

**Files:**
- Create: `src/mcpRoslyn/Tools/ReloadWorkspaceTool.cs`
- Create: `tests/mcpRoslyn.Tests/ToolTests/ReloadWorkspaceToolTests.cs`

**Step 1: Write the failing test**

```csharp
[Test]
public async Task ReloadWorkspace_returns_project_count_and_duration()
{
    var sut = await TestHost.CreateAsync<ReloadWorkspaceTool>();
    var result = await sut.InvokeAsync(CancellationToken.None);

    result.Error.Should().BeNull();
    result.Result.Should().NotBeNull();
    result.Result!.Loaded.Should().BeTrue();
    result.Result.ProjectCount.Should().Be(2);
    result.Result.DurationMs.Should().BeGreaterThan(0);
}
```

(`TestHost` is a small helper introduced in this task — see Step 3.)

**Step 2: Run test → FAIL** (compile error: ReloadWorkspaceTool doesn't exist)

**Step 3: Add `TestHost.cs`**

```csharp
namespace McpRoslyn.Tests.TestHelpers;

internal static class TestHost
{
    public static async Task<T> CreateAsync<T>() where T : class
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var workspace = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await workspace.LoadAsync();

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IWorkspaceService>(workspace);
        services.AddSingleton<T>();
        services.AddLogging();
        return services.BuildServiceProvider().GetRequiredService<T>();
    }
}
```

**Step 4: Implement `ReloadWorkspaceTool`**

```csharp
public sealed record ReloadResult(bool Loaded, int ProjectCount, long DurationMs);

[McpServerToolType]
internal sealed class ReloadWorkspaceTool(IWorkspaceService ws, ILogger<ReloadWorkspaceTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "reload_workspace")]
    [Description("Re-runs MSBuild evaluation on the solution. Call after .csproj/.sln changes.")]
    public Task<ToolResult<ReloadResult>> InvokeAsync(CancellationToken ct)
        => ExecuteAsync(async ct2 =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Workspace.ReloadAsync(ct2);
            return ToolResult<ReloadResult>.Ok(
                new ReloadResult(true, Workspace.LoadedProjectCount, sw.ElapsedMilliseconds));
        }, ct);
}
```

**Step 5: Run test → PASS**

Run: `dotnet test mcpRoslyn.sln --filter "FullyQualifiedName~ReloadWorkspaceToolTests" -c Release`

**Step 6: Commit**

```
git add src/mcpRoslyn/Tools/ReloadWorkspaceTool.cs tests/mcpRoslyn.Tests/ToolTests/ReloadWorkspaceToolTests.cs tests/mcpRoslyn.Tests/TestHelpers/TestHost.cs
git commit -m "feat: reload_workspace tool"
```

---

### Task 10: `ListDocumentSymbolsTool`

Pure syntax-tree walk — no `SymbolFinder`. Good warm-up for Roslyn API.

**Input:** `{ filePath }`. **Output:** `{ symbols: SymbolInfo[] }` of all top-level types + members.

**Test fixture:** `EnglishGreeter.cs` should yield 2 symbols (the class `EnglishGreeter` + its method `Greet`).

Roslyn approach: get `SyntaxRoot`, walk `MemberDeclarationSyntax` nodes, call `SemanticModel.GetDeclaredSymbol` on each, map via `RoslynHelpers.ToSymbolInfo`.

**TDD cycle:** test asserting exact symbol count + names → implement → pass → commit `feat: list_document_symbols tool`.

---

### Task 11: `WorkspaceSymbolTool`

**Input:** `{ query, kinds?, maxResults? }`. **Output:** `{ symbols: SymbolInfo[] }`.

Roslyn: `SymbolFinder.FindSourceDeclarationsWithPatternAsync(project, query, SymbolFilter.Type | SymbolFilter.Member, ct)` across all projects, dedupe by `SymbolId`, take first `maxResults` (default 100).

**Test:** query `"Greeter"` should return at least `IGreeter`, `EnglishGreeter`, `DutchGreeter` (3 symbols, kind filter `["Interface","Class"]`).

**TDD cycle** as above. Commit `feat: workspace_symbol tool`.

---

### Task 12: `GotoDefinitionTool`

**Input:** `{ filePath, line, column }`. **Output:** `{ definitions: SymbolLocation[] }`.

Roslyn: resolve symbol at position → return all `symbol.Locations` that are in source, mapped via `ToLocation`.

**Test:** point at `IGreeter` usage in `EnglishGreeter.cs` (`public class EnglishGreeter : IGreeter` — column on `IGreeter`). Expect 1 definition pointing at `IGreeter.cs` line 3.

**Test 2:** point at `PartialThing` usage from a hypothetical caller (or just verify `find_definitions` on a partial class via `workspace_symbol` lookup returns 2 locations). Use `find_implementations` semantics — actually safest: query `Shape` (abstract class) and confirm 1 definition. Save the multi-location partial-class case for `find_implementations` test instead.

**TDD cycle.** Commit `feat: goto_definition tool`.

---

### Task 13: `HoverTool`

**Input:** `{ filePath, line, column }`. **Output:** `{ symbol: SymbolInfo, xmlDocSummary?, signature }`.

Roslyn: resolve symbol → `symbol.GetDocumentationCommentXml()` parsed for `<summary>` text → `symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)` for signature.

**Test:** point at `EnglishGreeter.Greet` method name. Expect symbol kind `Method`, signature contains `string Greet(string name)`.

**TDD cycle.** Commit `feat: hover tool`.

---

### Task 14: `FindReferencesTool`

**Input:** `{ filePath, line, column }` OR `{ symbolId }`. **Output:** `{ symbol, references: SymbolLocation[] }`.

Roslyn: resolve symbol → `SymbolFinder.FindReferencesAsync(symbol, solution)` → flatten `ReferenceLocation`s into `SymbolLocation[]`.

**Test:** find references to `IGreeter`. Expect ≥ 3 (the interface declaration line + `EnglishGreeter : IGreeter` + `DutchGreeter : IGreeter` + the `IGreeter greeter` line in `TestApp/Program.cs`). Assert at least 3 results, all in fixture files.

**TDD cycle.** Commit `feat: find_references tool`.

---

### Task 15: `FindImplementationsTool`

**Input:** `{ filePath, line, column }` OR `{ symbolId }`. **Output:** `{ implementations: SymbolLocation[] }`.

Roslyn: resolve symbol → `SymbolFinder.FindImplementationsAsync(symbol, solution)`. Works on interface members AND on the interface type itself.

**Test:** point at `IGreeter` declaration. Expect exactly 2 implementations: `EnglishGreeter`, `DutchGreeter`.

**TDD cycle.** Commit `feat: find_implementations tool`.

---

### Task 16: `FindDerivedTypesTool`

**Input:** `{ filePath, line, column }` OR `{ symbolId }`, `transitive?: true`. **Output:** `{ derivedTypes: SymbolInfo[] }`.

Roslyn: if symbol is `INamedTypeSymbol` and class → `SymbolFinder.FindDerivedClassesAsync`; if interface → `FindDerivedInterfacesAsync`. Else return empty.

**Test:** point at `Shape` declaration. Expect 2 derived: `Circle`, `Square`.

**TDD cycle.** Commit `feat: find_derived_types tool`.

---

### Task 17: `FindCallersTool`

**Input:** `{ filePath, line, column }` OR `{ symbolId }`, `transitive?: false`. **Output:** `{ callers: { caller: SymbolInfo, callSite: SymbolLocation }[] }`.

Roslyn: resolve symbol (method) → `SymbolFinder.FindCallersAsync(symbol, solution)` → for each `SymbolCallerInfo`, map `CallingSymbol` → `SymbolInfo` and `Locations[0]` → `SymbolLocation`.

**Test:** find callers of `EnglishGreeter.Greet`. The fixture's `TestApp/Program.cs` calls `greeter.Greet("World")` on an `IGreeter` reference — note callers find direct invocations, so callers of `EnglishGreeter.Greet` may be 0 while callers of `IGreeter.Greet` are 1. Use `IGreeter.Greet` for the test to get a stable 1-caller result.

**TDD cycle.** Commit `feat: find_callers tool`.

---

### Task 18: `GetDocumentDiagnosticsTool`

**Input:** `{ filePath, severity? }`. **Output:** `{ diagnostics: DiagnosticInfo[] }`.

Roslyn: locate document → `await document.GetSemanticModelAsync()` → `semanticModel.GetDiagnostics()` → filter by severity → map.

**Test:** target `TestApp/BrokenClass.cs`. Expect ≥ 1 Error diagnostic with code starting `CS`.

**TDD cycle.** Commit `feat: get_document_diagnostics tool`.

---

### Task 19: `GetCompilationErrorsTool`

**Input:** `{ severity?, projectName? }`. **Output:** `{ diagnostics: DiagnosticInfo[] }`.

Roslyn: for each project (filter by name if given), `await project.GetCompilationAsync()` → `compilation.GetDiagnostics()` → filter severity → flatten + map.

**Test:** call without filters; expect ≥ 1 Error diagnostic from `TestApp` (the broken file). Call with `projectName: "TestLib"`; expect 0 errors.

**TDD cycle.** Commit `feat: get_compilation_errors tool`.

---

### Task 20: `SemanticSearchTool`

**Input:** `{ pattern }`. **Output:** `{ matches: SymbolInfo[] }`. Parser for 5 patterns:

| Pattern prefix | Implementation |
|---|---|
| `derives-from:` | Resolve target type symbol → `SymbolFinder.FindDerivedClassesAsync` |
| `implements:` | Resolve target type → `FindImplementationsAsync` |
| `has-attribute:` | Walk compilations: for each `INamedTypeSymbol`/`IMethodSymbol`, check `GetAttributes()` for matching `AttributeClass` full name |
| `returns:` | Walk all `IMethodSymbol`s in all compilations, filter where `ReturnType.ToDisplayString() == target` |
| `parameter-type:` | Walk all `IMethodSymbol`s, filter where any `Parameter.Type.ToDisplayString() == target` |

Unknown prefix → return `ToolError("INVALID_PATTERN", ...)`.

**Tests (5 patterns + 1 invalid = 6 tests in one fixture):**

```csharp
[TestCase("derives-from:TestLib.Shape", 2, TestName = "derives-from finds Circle and Square")]
[TestCase("implements:TestLib.IGreeter", 2, TestName = "implements finds 2 greeters")]
[TestCase("has-attribute:TestLib.MyMarkerAttribute", 2, TestName = "has-attribute finds MarkedType and MarkedMethod")]
[TestCase("returns:System.Int32", 2, TestName = "returns int finds PartialThing.Foo and Bar")]
[TestCase("parameter-type:System.String", 2, TestName = "parameter-type string finds both Greet methods")]
public async Task SemanticSearch_pattern_returns_expected_count(string pattern, int expected)
{ ... }

[Test]
public async Task SemanticSearch_invalid_pattern_returns_error()
{
    var sut = await TestHost.CreateAsync<SemanticSearchTool>();
    var result = await sut.InvokeAsync("garbage:foo", CancellationToken.None);
    result.Error.Should().NotBeNull();
    result.Error!.Code.Should().Be("INVALID_PATTERN");
}
```

**TDD cycle:** one test → one pattern impl at a time (six mini-cycles). Or one big test+impl cycle if the developer is comfortable.

Commit `feat: semantic_search tool with 5 pattern types`.

---

### Task 21: `RenameSymbolTool` (preview by default)

**Input:** `{ filePath, line, column, newName, applyEdits?: false }`. **Output:** `{ edits, conflicts? }` where `edits: { filePath, oldText, newText, location: SymbolLocation }[]`.

Roslyn:
- Resolve symbol at position.
- `Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, ct)` → returns new `Solution`.
- Compute the diff between original and renamed `Solution`: for each `DocumentId` that has changes, diff old vs new text by `TextChange`s, project to `{ filePath, oldText, newText, location }`.
- If `applyEdits == true`: write each modified document's full text back to its `FilePath`. Otherwise return the preview only.
- Conflict detection: `Renamer.RenameSymbolAsync` reports conflicts via the result; surface as `conflicts` field. (Use overload that returns `RenameResult` if available, else inspect `solution.Workspace.Diagnostics` afterwards.)

**Test 1: preview mode**

```csharp
[Test]
public async Task RenameSymbol_preview_returns_edits_without_writing()
{
    // rename EnglishGreeter.Greet to GreetUser, applyEdits=false
    // assert: ≥ 1 edit, original file unchanged on disk
}
```

**Test 2: apply mode**

```csharp
[Test]
public async Task RenameSymbol_applyEdits_writes_files()
{
    // rename DutchGreeter to NederlandseGreeter, applyEdits=true
    // assert: file content on disk reflects rename
    // TEARDOWN: restore original file content
}
```

**Test 3: conflict**

```csharp
[Test]
public async Task RenameSymbol_to_existing_member_name_returns_conflicts()
{
    // rename PartialThing.Foo to Bar (both exist) → expect non-empty conflicts
}
```

**TDD cycles** for each (or batch). Commit `feat: rename_symbol tool with preview default and apply mode`.

---

## Phase 3 — Packaging + acceptance

### Task 22: Publish self-contained exe

**Step 1: Add publish profile or just document the command**

In `mcpRoslyn.csproj` add:
```xml
<PropertyGroup>
  <PublishSingleFile>true</PublishSingleFile>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
</PropertyGroup>
```

**Step 2: Publish**

Run:
```
dotnet publish src/mcpRoslyn -c Release -o c:\projects\mcpRoslyn\bin\publish
```

Expected: `c:\projects\mcpRoslyn\bin\publish\mcpRoslyn.exe` produced.

**Step 3: Wire into duetGPT's `.mcp.json`**

Modify (or create) `c:\projects\duetgpt\duetGPT\.mcp.json`:
```json
{
  "mcpServers": {
    "mcpRoslyn": {
      "command": "c:\\projects\\mcpRoslyn\\bin\\publish\\mcpRoslyn.exe",
      "args": ["--solution", "c:\\projects\\duetgpt\\duetGPT\\duetGPT.sln"]
    }
  }
}
```

**Step 4: Commit (in mcpRoslyn repo) the publish-profile changes**

```
git add src/mcpRoslyn/mcpRoslyn.csproj
git commit -m "chore: configure self-contained win-x64 publish"
```

(`.mcp.json` change in duetGPT is a separate concern, committed in that repo.)

---

### Task 23: Manual acceptance against `duetGPT.sln`

This task is non-automated. Goal: validate that the 4 reference queries return correct results.

**Step 1: Start a fresh Claude Code session in `c:\projects\duetgpt\duetGPT\`** so the new `.mcp.json` is picked up.

**Step 2: Invoke each tool. Record actual vs expected.**

| Query | Expected |
|---|---|
| `find_references` on `IGroupPermissionResolver` | ≥ 3 hits in `Services\` and at least 1 in `Controllers\` |
| `find_implementations` on `IBuiltInToolProvider` | 1 hit: `DatasourceToolProvider` |
| `find_callers` on `KnowledgeService.ExpandQueryAsync` | At least the call site inside `KnowledgeService.SearchAsync` |
| `semantic_search has-attribute:McpServerToolType` | 1 hit per future tool (in mcpRoslyn itself), 0 in duetGPT — confirms the search finds nothing because duetGPT doesn't use this attribute |

**Step 3: Note cold-start time** for the duetGPT.sln load. If > 60s, log it as a follow-up to optimize.

**Step 4: Note any tool surface gaps** discovered in real use. Append to `docs/plans/2026-05-15-mcproslyn-design.md` under "Open questions / deferred" section.

**Step 5: Commit acceptance log**

Create `docs/acceptance/2026-05-15-v1-acceptance.md` with the 4 query results + any notes. Commit.

```
git add docs/acceptance/
git commit -m "docs: v1 manual acceptance log against duetGPT.sln"
```

---

## Task summary

| # | Task | Type |
|---|---|---|
| 1 | Scaffold solution + projects | Foundation |
| 2 | Wire MSBuildLocator + DI host | Foundation |
| 3 | Build fixture test solution | Foundation |
| 4 | Contracts (DTOs) | Foundation |
| 5 | IWorkspaceService + skeleton | Foundation |
| 6 | Test: LoadAsync loads fixture | Foundation (TDD) |
| 7 | Mtime-based refresh | Foundation (TDD) |
| 8 | ToolBase + RoslynHelpers | Foundation |
| 9 | reload_workspace tool | Tool (TDD) |
| 10 | list_document_symbols tool | Tool (TDD) |
| 11 | workspace_symbol tool | Tool (TDD) |
| 12 | goto_definition tool | Tool (TDD) |
| 13 | hover tool | Tool (TDD) |
| 14 | find_references tool | Tool (TDD) |
| 15 | find_implementations tool | Tool (TDD) |
| 16 | find_derived_types tool | Tool (TDD) |
| 17 | find_callers tool | Tool (TDD) |
| 18 | get_document_diagnostics tool | Tool (TDD) |
| 19 | get_compilation_errors tool | Tool (TDD) |
| 20 | semantic_search tool (5 patterns) | Tool (TDD) |
| 21 | rename_symbol tool (preview + apply) | Tool (TDD) |
| 22 | Publish self-contained exe | Packaging |
| 23 | Manual acceptance vs duetGPT.sln | Validation |

**Estimated commit count:** 23 (one per task). **Estimated test count after Task 21:** ~20 NUnit tests, all green.

---

## Risks & traps

- **MSBuildLocator must be called before any Roslyn type touches the AppDomain.** Putting it in a `static` constructor of a class that depends on Roslyn is too late. Put it at the very top of `Main` (Task 2 does this).
- **Fixture solution shouldn't be built by `dotnet build`.** `<None Include>` (not `<Compile>`) makes MSBuild copy the files without compiling them. Confirm `dotnet build` doesn't touch the fixture's bin/obj.
- **Path comparison on Windows.** `Path.GetFullPath` + `OrdinalIgnoreCase` (helper in `RoslynHelpers.FindDocument`). Roslyn's `Document.FilePath` casing can differ from caller's argument.
- **`DocumentationCommentId` is null for some symbols** (anonymous types, lambdas). Empty-string fallback in `ToSymbolInfo` is intentional; callers that branch on `SymbolId` need to handle the empty case.
- **stdout corruption.** Any unintentional `Console.WriteLine` or `Console.Out` write outside the MCP SDK will break the protocol stream. Every `ILogger` must be wired to stderr (the `LogToStandardErrorThreshold = LogLevel.Trace` setting in Task 2 takes care of this).
- **Test isolation.** Tests that mutate fixture files MUST restore them in a `finally` block. If a test leaves the fixture dirty, every later test sees the dirty version. Consider a `[TearDown]` that resets the fixture from a backup if this becomes a recurring problem.
- **MSBuildWorkspace startup cost.** First load can hit 30s on big solutions. Tests use the small fixture so this is fine; manual acceptance on duetGPT.sln will be slow on first call. Document the timing in acceptance.
