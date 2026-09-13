using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
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
    private readonly object _gate = new();        // guards the collections above and _solution
    private readonly object _refreshGate = new(); // one refresh at a time

    private static readonly HashSet<string> RouteMethods = new(StringComparer.Ordinal)
        { "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "Map" };
    private static readonly HashSet<string> DiMethods = new(StringComparer.Ordinal)
        { "AddSingleton", "AddTransient", "AddScoped" };

    private Solution? _solution; // holds the dirty documents' current text

    /// <summary>Entries collected without the lock and published in one step.</summary>
    private sealed class DocEntries
    {
        public readonly List<RouteEntry> Routes = new();
        public readonly List<MiddlewareEntry> Middleware = new();
        public readonly List<HostedServiceEntry> HostedServices = new();
        public readonly List<DiEntry> Registrations = new();
        public readonly List<DiEntry> Unclassified = new();
    }

    public async Task BuildAsync(Solution solution, CancellationToken ct = default)
    {
        lock (_gate) _solution ??= solution; // an Invalidate during warm-up already holds a newer one
        var tasks = solution.Projects.Select(async project =>
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) return;

            foreach (var doc in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                var tree = await doc.GetSyntaxTreeAsync(ct);
                if (tree is null) continue;
                var entries = new DocEntries();
                IndexDocument(doc.Id, tree, compilation.GetSemanticModel(tree), entries);
                lock (_gate) Publish(entries);
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Records changed documents together with the solution holding their new text, in one step, so a
    /// refresh never pairs a dirty marker with a solution that predates it.
    /// </summary>
    public void Invalidate(IEnumerable<DocumentId> changed, Solution solution)
    {
        lock (_gate)
        {
            _dirty.UnionWith(changed);
            _solution = solution;
        }
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

    // -------- Dirty re-walk --------

    /// <summary>
    /// Re-indexes the changed documents into locals, then swaps them in under the lock: a concurrent
    /// query sees the old entries or the new ones, never neither, and a re-bind that throws leaves the
    /// old entries and the markers in place (IDX-004). Markers are cleared only if no newer solution
    /// arrived during the walk; otherwise the next query walks again.
    /// ponytail: synchronous re-bind — the query API is synchronous; make it async if a caller needs to.
    /// </summary>
    private void RefreshDirty()
    {
        lock (_refreshGate)
        {
            HashSet<DocumentId> dirty;
            Solution solution;
            lock (_gate)
            {
                if (_dirty.Count == 0 || _solution is null) return;
                dirty = new HashSet<DocumentId>(_dirty);
                solution = _solution;
            }

            var fresh = new DocEntries();
            foreach (var docId in dirty)
            {
                var doc = solution.GetDocument(docId);
                if (doc is null) continue; // gone from the solution: its entries simply go
                var tree = doc.GetSyntaxTreeAsync().GetAwaiter().GetResult();
                var sem = doc.GetSemanticModelAsync().GetAwaiter().GetResult();
                if (tree is null || sem is null) continue;
                IndexDocument(docId, tree, sem, fresh);
            }

            lock (_gate)
            {
                _routes.RemoveAll(e => dirty.Contains(e.DocumentId));
                _middleware.RemoveAll(e => dirty.Contains(e.DocumentId));
                _hostedServices.RemoveAll(e => dirty.Contains(e.DocumentId));
                _registrations.RemoveAll(e => dirty.Contains(e.DocumentId));
                _unclassified.RemoveAll(e => dirty.Contains(e.DocumentId));
                Publish(fresh);
                if (ReferenceEquals(_solution, solution)) _dirty.ExceptWith(dirty);
            }
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void Publish(DocEntries entries)
    {
        _routes.AddRange(entries.Routes);
        _middleware.AddRange(entries.Middleware);
        _hostedServices.AddRange(entries.HostedServices);
        _registrations.AddRange(entries.Registrations);
        _unclassified.AddRange(entries.Unclassified);
    }

    // -------- Indexing one document (used by both initial build and dirty re-walk) --------

    private void IndexDocument(DocumentId docId, SyntaxTree tree, SemanticModel semantic, DocEntries into)
    {
        var root = tree.GetRoot();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetInvokedMethodName(invocation);
            if (methodName is null) continue;

            if (RouteMethods.Contains(methodName))
            {
                var route = TryBuildRoute(invocation, semantic, docId, methodName);
                if (route is not null) into.Routes.Add(route);
            }
            else if (methodName.StartsWith("Use", StringComparison.Ordinal) && IsApplicationBuilderCall(invocation, semantic))
            {
                var loc = ToLocation(invocation);
                if (loc is not null) into.Middleware.Add(new MiddlewareEntry(methodName, docId, loc));
            }
            else if (methodName == "AddHostedService")
            {
                var entry = TryBuildHostedService(invocation, semantic, docId);
                if (entry is not null) into.HostedServices.Add(entry);
            }
            else if (DiMethods.Contains(methodName))
            {
                var entry = TryBuildDi(invocation, semantic, docId, methodName);
                if (entry is not null) into.Registrations.Add(entry);
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
                into.Unclassified.Add(new DiEntry(
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
            into.HostedServices.Add(new HostedServiceEntry(
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

        // From the bound method, not the syntax, so an inferred AddHostedService(sp => new Worker()) registers Worker.
        var t = sem.GetSymbolInfo(inv).Symbol is IMethodSymbol { IsGenericMethod: true, TypeArguments.Length: 1 } method
            ? TypeName(method.TypeArguments[0])
            : null;
        var loc = ToLocation(inv);
        if (loc is null || t is null) return null;
        return new HostedServiceEntry("registered", t, null, null, docId, loc);
    }

    private static DiEntry? TryBuildDi(InvocationExpressionSyntax inv, SemanticModel sem, DocumentId docId, string methodName)
    {
        if (!IsServiceCollectionCall(inv, sem)) return null;

        var lifetime = methodName.Substring(3); // "Singleton" / "Transient" / "Scoped"
        string? serviceType = null, implType = null;

        // Types come from the bound method, not the syntax: AddSingleton(options) and AddTransient(sp => new Foo())
        // infer their type arguments, and the non-generic overloads name types as typeof operands. Reading explicit
        // generic syntax only left those entries with no type, so a registered type read as unregistered (TOOL-014).
        if (sem.GetSymbolInfo(inv).Symbol is IMethodSymbol method)
        {
            if (method.IsGenericMethod)
            {
                var args = method.TypeArguments;
                if (args.Length >= 1) serviceType = TypeName(args[0]);
                if (args.Length >= 2) implType    = TypeName(args[1]);
                // AddSingleton<IFoo>(sp => new Foo()) and AddSingleton<IFoo>(new Foo()) name the implementation only in
                // their argument: the one type the factory returns, or the instance's static type.
                if (args.Length == 1 && sem.GetOperation(inv) is IInvocationOperation withArguments)
                    implType = ImplementationFromArguments(withArguments);
            }
            else if (sem.GetOperation(inv) is IInvocationOperation call)
            {
                // By the parameter each typeof binds to, so named arguments in any order map correctly.
                foreach (var arg in call.Arguments)
                {
                    if (arg.Value is not ITypeOfOperation typeOf) continue;
                    if (arg.Parameter?.Name == "serviceType") serviceType = TypeName(typeOf.TypeOperand);
                    else if (arg.Parameter?.Name == "implementationType") implType = TypeName(typeOf.TypeOperand);
                }
                // AddSingleton(typeof(IFoo), new Foo()) and AddScoped(typeof(IFoo), sp => new Foo()).
                implType ??= ImplementationFromArguments(call);
            }
        }

        var loc = ToLocation(inv);
        if (loc is null) return null;
        return new DiEntry(serviceType, implType, lifetime, inv.ToString(), docId, loc);
    }

    private static string? TypeName(ITypeSymbol? type)
        => type is null or { TypeKind: TypeKind.Error } ? null : type.ToDisplayString();

    /// <summary>
    /// The implementation a single-type-argument registration names only through its argument: the static type of an
    /// instance, or the one type a factory lambda returns. Null when there is no such argument, or when the factory's
    /// returns disagree — an unknown implementation, not a guessed one.
    /// </summary>
    private static string? ImplementationFromArguments(IInvocationOperation call)
    {
        foreach (var arg in call.Arguments)
        {
            var value = WithoutConversions(arg.Value);
            if (arg.Parameter?.Name == "implementationInstance") return TypeName(value.Type);
            if (value is not IDelegateCreationOperation { Target: IAnonymousFunctionOperation factory }) continue;

            var returned = factory.Body.Descendants().OfType<IReturnOperation>()
                .Where(r => r.ReturnedValue is not null && ReturnsFrom(r, factory))
                .Select(r => WithoutConversions(r.ReturnedValue!).Type)
                .Distinct(SymbolEqualityComparer.Default)
                .ToArray();
            return returned.Length == 1 ? TypeName(returned[0] as ITypeSymbol) : null;
        }
        return null;

        // Only the compiler's implicit conversions (Foo to IFoo or object) are looked through. An explicit cast decides
        // the type, and a user-defined conversion can produce a different object from its operand.
        static IOperation WithoutConversions(IOperation op)
            => op is IConversionOperation { IsImplicit: true, Conversion.IsUserDefined: false } c ? WithoutConversions(c.Operand) : op;

        // A return inside a nested lambda or local function belongs to that function, not to the factory.
        static bool ReturnsFrom(IOperation op, IAnonymousFunctionOperation owner)
        {
            for (var parent = op.Parent; parent is not null; parent = parent.Parent)
            {
                if (ReferenceEquals(parent, owner)) return true;
                if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation) return false;
            }
            return false;
        }
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
