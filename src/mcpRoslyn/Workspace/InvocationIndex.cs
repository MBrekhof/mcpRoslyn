using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using mcpRoslyn.Contracts;
using mcpRoslyn.Tools;

namespace mcpRoslyn.Workspace;

public sealed record RouteEntry(
    string Verb,
    string? Template,
    string? Handler,
    DocumentId DocumentId,
    SymbolLocation Location);

public sealed record MiddlewareEntry(
    string Method,
    DocumentId DocumentId,
    SymbolLocation Location);

public sealed record HostedServiceEntry(
    string Kind,                    // "registered" or "subclass"
    string? ServiceType,            // set for "registered"
    string? Type,                   // set for "subclass"
    string? BaseType,               // set for "subclass"
    DocumentId DocumentId,
    SymbolLocation Location);

public sealed record DiEntry(
    string? ServiceType,
    string? ImplType,
    string? Lifetime,               // Singleton | Transient | Scoped | null for unclassified
    string RawCall,
    DocumentId DocumentId,
    SymbolLocation Location);

public sealed record DiQueryResult(
    IReadOnlyList<DiEntry> Registrations,
    IReadOnlyList<DiEntry> Unclassified);

public sealed class InvocationIndex
{
    private readonly List<RouteEntry> _routes = new();
    private readonly List<MiddlewareEntry> _middleware = new();
    private readonly List<HostedServiceEntry> _hostedServices = new();
    private readonly List<DiEntry> _registrations = new();
    private readonly List<DiEntry> _unclassified = new();
    private readonly HashSet<DocumentId> _dirty = new();
    private readonly object _gate = new();

    private static readonly HashSet<string> RouteMethods = new(StringComparer.Ordinal)
        { "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "Map" };
    private static readonly HashSet<string> DiMethods = new(StringComparer.Ordinal)
        { "AddSingleton", "AddTransient", "AddScoped" };

    private Solution? _solution;

    public async Task BuildAsync(Solution solution, CancellationToken ct = default)
    {
        _solution = solution;
        var tasks = solution.Projects.Select(async project =>
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) return;

            foreach (var doc in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                var tree = await doc.GetSyntaxTreeAsync(ct);
                if (tree is null) continue;
                var semantic = compilation.GetSemanticModel(tree);
                IndexDocument(doc.Id, tree, semantic);
            }

            // Walk symbols for BackgroundService subclasses
            foreach (var sym in WalkTypes(compilation.GlobalNamespace))
            {
                ct.ThrowIfCancellationRequested();
                if (IsBackgroundServiceSubclass(sym))
                {
                    var loc = sym.Locations
                        .Select(l => RoslynHelpers.ToLocation(l))
                        .FirstOrDefault(l => l is not null);
                    if (loc is null) continue;
                    var sourceTree = sym.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
                    if (sourceTree is null) continue;
                    var docId = solution.GetDocumentId(sourceTree);
                    if (docId is null) continue;
                    var entry = new HostedServiceEntry(
                        Kind: "subclass",
                        ServiceType: null,
                        Type: sym.ToDisplayString(),
                        BaseType: sym.BaseType?.Name,
                        DocumentId: docId,
                        Location: loc);
                    lock (_gate) _hostedServices.Add(entry);
                }
            }
        });
        await Task.WhenAll(tasks);
    }

    public void MarkDirty(DocumentId documentId)
    {
        lock (_gate) _dirty.Add(documentId);
    }

    /// <summary>
    /// Updates the current solution snapshot used by dirty re-walks.
    /// Called by WorkspaceService whenever it refreshes the solution.
    /// </summary>
    public void UpdateSolution(Solution solution)
    {
        // No lock needed — _solution is only read inside RefreshDirty which
        // is called from the public Query* methods; the assignment is atomic
        // (reference write on 64-bit CLR).
        _solution = solution;
    }

    public IReadOnlyList<RouteEntry> QueryRoutes()
    {
        RefreshDirty();
        lock (_gate) return new List<RouteEntry>(_routes);
    }

    public IReadOnlyList<MiddlewareEntry> QueryMiddleware()
    {
        RefreshDirty();
        lock (_gate) return new List<MiddlewareEntry>(_middleware);
    }

    public IReadOnlyList<HostedServiceEntry> QueryHostedServices()
    {
        RefreshDirty();
        lock (_gate) return new List<HostedServiceEntry>(_hostedServices);
    }

    public DiQueryResult QueryDi()
    {
        RefreshDirty();
        lock (_gate)
        {
            return new DiQueryResult(
                new List<DiEntry>(_registrations),
                new List<DiEntry>(_unclassified));
        }
    }

    // -------- Dirty re-walk (idempotent: removes existing entries for the doc, re-indexes the doc) --------

    private void RefreshDirty()
    {
        HashSet<DocumentId> dirtySnapshot;
        lock (_gate)
        {
            if (_dirty.Count == 0 || _solution is null) return;
            dirtySnapshot = new HashSet<DocumentId>(_dirty);
            _dirty.Clear();
        }

        foreach (var docId in dirtySnapshot)
        {
            // Remove any existing entries for this doc across all buckets
            lock (_gate)
            {
                _routes.RemoveAll(e => e.DocumentId == docId);
                _middleware.RemoveAll(e => e.DocumentId == docId);
                _hostedServices.RemoveAll(e => e.DocumentId == docId);
                _registrations.RemoveAll(e => e.DocumentId == docId);
                _unclassified.RemoveAll(e => e.DocumentId == docId);
            }

            var doc = _solution!.GetDocument(docId);
            if (doc is null) continue;
            var tree = doc.GetSyntaxTreeAsync().GetAwaiter().GetResult();
            if (tree is null) continue;
            var sem = doc.GetSemanticModelAsync().GetAwaiter().GetResult();
            if (sem is null) continue;

            IndexDocument(docId, tree, sem);
        }
    }

    // -------- Indexing one document (used by both initial build and dirty re-walk) --------

    private void IndexDocument(DocumentId docId, SyntaxTree tree, SemanticModel semantic)
    {
        var root = tree.GetRoot();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetInvokedMethodName(invocation);
            if (methodName is null) continue;

            if (RouteMethods.Contains(methodName))
            {
                var route = TryBuildRoute(invocation, semantic, docId, methodName);
                if (route is not null) lock (_gate) _routes.Add(route);
            }
            else if (methodName.StartsWith("Use", StringComparison.Ordinal) && IsApplicationBuilderCall(invocation, semantic))
            {
                var loc = ToLocation(invocation);
                if (loc is not null) lock (_gate) _middleware.Add(new MiddlewareEntry(methodName, docId, loc));
            }
            else if (methodName == "AddHostedService")
            {
                var entry = TryBuildHostedService(invocation, semantic, docId);
                if (entry is not null) lock (_gate) _hostedServices.Add(entry);
            }
            else if (DiMethods.Contains(methodName))
            {
                var entry = TryBuildDi(invocation, semantic, docId, methodName);
                if (entry is not null) lock (_gate) _registrations.Add(entry);
            }
            else if (IsServiceCollectionCall(invocation, semantic))
            {
                var loc = ToLocation(invocation);
                if (loc is null) continue;
                lock (_gate) _unclassified.Add(new DiEntry(
                    ServiceType: null,
                    ImplType: null,
                    Lifetime: null,
                    RawCall: invocation.ToString(),
                    DocumentId: docId,
                    Location: loc));
            }
        }
    }

    // -------- Detection helpers --------

    private static string? GetInvokedMethodName(InvocationExpressionSyntax inv) =>
        inv.Expression switch
        {
            MemberAccessExpressionSyntax m when m.Name is GenericNameSyntax g => g.Identifier.Text,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            GenericNameSyntax g            => g.Identifier.Text,
            IdentifierNameSyntax i         => i.Identifier.Text,
            _ => null
        };

    private static RouteEntry? TryBuildRoute(InvocationExpressionSyntax inv, SemanticModel sem, DocumentId docId, string methodName)
    {
        var verb = methodName switch
        {
            "MapGet" => "GET", "MapPost" => "POST", "MapPut" => "PUT",
            "MapDelete" => "DELETE", "MapPatch" => "PATCH", _ => "ANY"
        };

        string? template = null;
        if (inv.ArgumentList.Arguments.Count > 0 &&
            inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
            lit.IsKind(SyntaxKind.StringLiteralExpression))
        {
            template = lit.Token.ValueText;
        }

        string? handler = inv.ArgumentList.Arguments.Count > 1
            ? inv.ArgumentList.Arguments[1].Expression.ToString()
            : null;

        var loc = ToLocation(inv);
        if (loc is null) return null;
        return new RouteEntry(verb, template, handler, docId, loc);
    }

    private static bool IsApplicationBuilderCall(InvocationExpressionSyntax inv, SemanticModel sem)
    {
        if (inv.Expression is not MemberAccessExpressionSyntax m) return false;
        var receiverType = sem.GetTypeInfo(m.Expression).Type;
        if (receiverType is null) return false;
        return ImplementsOrIs(receiverType, "Microsoft.AspNetCore.Builder.IApplicationBuilder")
            || receiverType.ToDisplayString() == "Microsoft.AspNetCore.Builder.WebApplication";
    }

    private static HostedServiceEntry? TryBuildHostedService(InvocationExpressionSyntax inv, SemanticModel sem, DocumentId docId)
    {
        var genericName = inv.Expression switch
        {
            MemberAccessExpressionSyntax m when m.Name is GenericNameSyntax g => g,
            GenericNameSyntax g => g,
            _ => null
        };
        if (genericName is null || genericName.TypeArgumentList.Arguments.Count != 1) return null;

        var t = sem.GetSymbolInfo(genericName.TypeArgumentList.Arguments[0]).Symbol as INamedTypeSymbol;
        var loc = ToLocation(inv);
        if (loc is null || t is null) return null;
        return new HostedServiceEntry("registered", t.ToDisplayString(), null, null, docId, loc);
    }

    private static DiEntry? TryBuildDi(InvocationExpressionSyntax inv, SemanticModel sem, DocumentId docId, string methodName)
    {
        if (!IsServiceCollectionCall(inv, sem)) return null;

        var lifetime = methodName.Substring(3); // "Singleton" / "Transient" / "Scoped"
        string? serviceType = null, implType = null;

        var genericName = inv.Expression switch
        {
            MemberAccessExpressionSyntax m when m.Name is GenericNameSyntax g => g,
            GenericNameSyntax g => g,
            _ => null
        };
        if (genericName is not null)
        {
            var args = genericName.TypeArgumentList.Arguments;
            if (args.Count >= 1) serviceType = sem.GetSymbolInfo(args[0]).Symbol?.ToDisplayString();
            if (args.Count >= 2) implType    = sem.GetSymbolInfo(args[1]).Symbol?.ToDisplayString();
        }

        var loc = ToLocation(inv);
        if (loc is null) return null;
        return new DiEntry(serviceType, implType, lifetime, inv.ToString(), docId, loc);
    }

    private static bool IsServiceCollectionCall(InvocationExpressionSyntax inv, SemanticModel sem)
    {
        var symbol = sem.GetSymbolInfo(inv).Symbol as IMethodSymbol;
        if (symbol is null) return false;
        if (symbol.IsExtensionMethod && symbol.ReducedFrom is { Parameters.Length: > 0 } reduced)
            return reduced.Parameters[0].Type.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.IServiceCollection";
        if (symbol.ContainingType.ToDisplayString().Contains("ServiceCollection")) return true;
        if (inv.Expression is MemberAccessExpressionSyntax m)
        {
            var t = sem.GetTypeInfo(m.Expression).Type;
            return t is not null && ImplementsOrIs(t, "Microsoft.Extensions.DependencyInjection.IServiceCollection");
        }
        return false;
    }

    private static bool ImplementsOrIs(ITypeSymbol type, string fullName)
    {
        if (type.ToDisplayString() == fullName) return true;
        return type.AllInterfaces.Any(i => i.ToDisplayString() == fullName);
    }

    private static bool IsBackgroundServiceSubclass(INamedTypeSymbol type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == "Microsoft.Extensions.Hosting.BackgroundService") return true;
        return false;
    }

    private static SymbolLocation? ToLocation(SyntaxNode node)
    {
        if (node.SyntaxTree.FilePath is null) return null;
        var span = node.GetLocation().GetLineSpan();
        return new SymbolLocation(
            FilePath: node.SyntaxTree.FilePath,
            Line: span.StartLinePosition.Line + 1,
            Column: span.StartLinePosition.Character + 1,
            EndLine: span.EndLinePosition.Line + 1,
            EndColumn: span.EndLinePosition.Character + 1);
    }

    private static IEnumerable<INamedTypeSymbol> WalkTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol n)
                foreach (var t in WalkTypes(n)) yield return t;
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in type.GetTypeMembers()) yield return nested;
            }
        }
    }
}
