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
        lock (_gate)
        {
            // A partial subclass has one entry per declaring file; report it once per project —
            // same-named types in different projects are different types.
            var seen = new HashSet<(string Type, ProjectId Project)>();
            return _hostedServices
                .Where(h => h.Kind != "subclass" || seen.Add((h.Type!, h.DocumentId.ProjectId)))
                .ToList();
        }
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
            // ponytail: only bind names that follow DI-registration conventions.
            // The semantic GetSymbolInfo inside IsServiceCollectionCall is ~50µs/call;
            // running it on every invocation (98% are ordinary calls) cost 5-13s of
            // warm-up on real solutions (issue #1). Real IServiceCollection extensions
            // are named Add*/TryAdd*/Configure*/Replace/Decorate by convention.
            else if (LooksLikeDiVerb(methodName) && IsServiceCollectionCall(invocation, semantic))
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

        // BackgroundService subclasses are found here, per document, not by walking the
        // compilation's namespaces: RefreshDirty re-indexes through this method, so a subclass
        // has to be re-found when its file changes (IDX-003). The namespace walk also covered
        // every referenced assembly — the waste PERF-001 removed from SymbolIndex.
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (semantic.GetDeclaredSymbol(cls) is not INamedTypeSymbol type || !IsBackgroundServiceSubclass(type))
                continue;
            // A partial class is recorded by every file declaring it (QueryHostedServices reports it
            // once), so editing any part — including adding the base list to a non-first part —
            // re-finds it. ponytail: removing the base from one part leaves the other parts' entries
            // until those files change or a reload; dirtiness is per document (IDX-004 ceiling).
            var loc = RoslynHelpers.ToLocation(cls.Identifier.GetLocation());
            if (loc is null) continue;
            lock (_gate) _hostedServices.Add(new HostedServiceEntry(
                Kind: "subclass",
                ServiceType: null,
                Type: type.ToDisplayString(),
                BaseType: type.BaseType?.Name,
                DocumentId: docId,
                Location: loc));
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
        // TOOL-009: the name alone is not enough — AutoMapper's Map<T>(source) is the common
        // look-alike. A route is an extension on IEndpointRouteBuilder (WebApplication, route groups),
        // called as one (app.MapGet) or statically (EndpointRouteBuilderExtensions.MapGet(app, …)).
        if (ResolveMethod(inv, sem) is not { } method) return null;
        var extension = method.ReducedFrom ?? (method.IsExtensionMethod ? method : null);
        if (extension is not { Parameters.Length: > 0 }
            || !ImplementsOrIs(extension.Parameters[0].Type, "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder"))
            return null;

        // The parameters after the builder. A static call passes the builder as an argument too,
        // so its parameter list (and argument positions) still include it.
        var routeParameters = method.ReducedFrom is null ? method.Parameters.Skip(1).ToList() : method.Parameters.ToList();
        var handlerParameter = routeParameters.FirstOrDefault(p =>
            p.Type.ToDisplayString() is "System.Delegate" or "Microsoft.AspNetCore.Http.RequestDelegate");
        // Every endpoint takes a handler; a same-named IEndpointRouteBuilder extension without one isn't a route.
        if (handlerParameter is null) return null;

        // Arguments by parameter, not by position: MapMethods puts the HTTP methods before the
        // handler, and callers may name their arguments.
        var pattern = ArgumentFor(inv, method, routeParameters.FirstOrDefault());
        var httpMethods = ArgumentFor(inv, method, routeParameters.FirstOrDefault(p =>
            p.Type.ToDisplayString() == "System.Collections.Generic.IEnumerable<string>"));
        var handler = ArgumentFor(inv, method, handlerParameter);

        var verb = methodName switch
        {
            "MapGet" => "GET", "MapPost" => "POST", "MapPut" => "PUT",
            "MapDelete" => "DELETE", "MapPatch" => "PATCH",
            // ponytail: literal method lists only; HttpMethods.Get constants read as ANY.
            "MapMethods" => LiteralStrings(httpMethods) is { } verbs ? string.Join(",", verbs) : "ANY",
            _ => "ANY"
        };
        var template = pattern is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText
            : null;

        var loc = ToLocation(inv);
        if (loc is null) return null;
        return new RouteEntry(verb, template, handler?.ToString(), docId, loc);
    }

    private static ExpressionSyntax? ArgumentFor(InvocationExpressionSyntax inv, IMethodSymbol method, IParameterSymbol? parameter)
    {
        if (parameter is null) return null;
        var args = inv.ArgumentList.Arguments;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].NameColon is { } named)
            {
                // ValueText, not Text: `@pattern:` names the parameter `pattern`.
                if (named.Name.Identifier.ValueText == parameter.Name) return args[i].Expression;
            }
            else if (i < method.Parameters.Length && SymbolEqualityComparer.Default.Equals(method.Parameters[i], parameter))
            {
                return args[i].Expression;
            }
        }
        return null;
    }

    /// <summary>
    /// The invoked method; when overload resolution failed (an error inside a lambda, say), the
    /// candidate whose parameter names fit the caller's named arguments, so arguments are read
    /// against the layout the caller meant.
    /// </summary>
    private static IMethodSymbol? ResolveMethod(InvocationExpressionSyntax inv, SemanticModel sem)
    {
        var info = sem.GetSymbolInfo(inv);
        if (info.Symbol is IMethodSymbol resolved) return resolved;
        var candidates = info.CandidateSymbols.OfType<IMethodSymbol>().ToList();
        var names = inv.ArgumentList.Arguments
            .Where(a => a.NameColon is not null)
            .Select(a => a.NameColon!.Name.Identifier.ValueText)
            .ToList();
        return candidates.FirstOrDefault(c => names.All(n => c.Parameters.Any(p => p.Name == n)))
               ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// The strings of an array or collection literal when every element is a string literal;
    /// null otherwise, so a partly computed list reads as ANY instead of an authoritative wrong answer.
    /// </summary>
    private static List<string>? LiteralStrings(ExpressionSyntax? expression)
    {
        IEnumerable<ExpressionSyntax?>? elements = expression switch
        {
            ImplicitArrayCreationExpressionSyntax a => a.Initializer.Expressions,
            ArrayCreationExpressionSyntax { Initializer: { } init } => init.Expressions,
            CollectionExpressionSyntax c => c.Elements.Select(e => (e as ExpressionElementSyntax)?.Expression),
            _ => null
        };
        if (elements is null) return null;

        var values = new List<string>();
        foreach (var element in elements)
        {
            if (element is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.StringLiteralExpression)) return null;
            values.Add(lit.Token.ValueText);
        }
        return values.Count > 0 ? values : null;
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
        // TOOL-009: only the IServiceCollection extension registers a hosted service.
        if (!IsServiceCollectionCall(inv, sem)) return null;

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

    // Cheap syntactic gate for the Unclassified-DI fallback (issue #1). A superset of
    // real IServiceCollection extension names; the semantic confirm filters the rest out.
    private static bool LooksLikeDiVerb(string name) =>
        name.StartsWith("Add", StringComparison.Ordinal)
        || name.StartsWith("TryAdd", StringComparison.Ordinal)
        || name.StartsWith("Configure", StringComparison.Ordinal)
        || name.StartsWith("PostConfigure", StringComparison.Ordinal)
        || name is "Replace" or "Decorate" or "RemoveAll";

    private static bool IsServiceCollectionCall(InvocationExpressionSyntax inv, SemanticModel sem)
    {
        var symbol = sem.GetSymbolInfo(inv).Symbol as IMethodSymbol;
        if (symbol is null) return false;
        // An extension on IServiceCollection, called as one (services.AddX()) or statically
        // (ServiceCollectionServiceExtensions.AddSingleton(services, …)). No type-name shortcut:
        // a class that merely has "ServiceCollection" in its name is not one (TOOL-009).
        var extension = symbol.ReducedFrom ?? (symbol.IsExtensionMethod ? symbol : null);
        if (extension is { Parameters.Length: > 0 })
            return ImplementsOrIs(extension.Parameters[0].Type, "Microsoft.Extensions.DependencyInjection.IServiceCollection");
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
}
