using Microsoft.CodeAnalysis;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

/// <summary>
/// "Is this type registered?" against every piece of registration evidence the invocation index holds: classified
/// DI registrations, AddHostedService registrations, and unclassified DI calls. find_dead_code_candidates and
/// find_registrations share it, so both tools mean the same thing by "registered".
/// </summary>
internal sealed class RegistrationLookup
{
    private readonly HashSet<string> _names;
    private readonly IReadOnlyList<string> _rawCalls;

    /// <param name="typeNames">Fully qualified type names from registrations, constructed or not.</param>
    /// <param name="rawCalls">Source text of DI registration calls, classified or not.</param>
    internal RegistrationLookup(IEnumerable<string> typeNames, IReadOnlyList<string> rawCalls)
    {
        // Registrations name constructed types (Repo<Foo>) and SymbolIndex declared ones (Repo<T>), so both sides
        // are reduced to their generic definition.
        _names = typeNames.Select(RoslynHelpers.GenericDefinitionName).ToHashSet(StringComparer.Ordinal);
        _rawCalls = rawCalls;
    }

    public static RegistrationLookup Build(InvocationIndex index) => From(index.QueryDi(), index.QueryHostedServices());

    internal static RegistrationLookup From(DiQueryResult di, IEnumerable<HostedServiceEntry> hostedServices)
    {
        var names = di.Registrations.SelectMany(e => new[] { e.ServiceType, e.ImplType })
            // AddHostedService<T> lands in the hosted-service index, not the DI one. That index also lists every
            // BackgroundService subclass, registered or not, so only its "registered" entries count.
            .Concat(hostedServices.Where(h => h.Kind == "registered").Select(h => h.ServiceType))
            .OfType<string>();
        // Source text is evidence only where the index resolved no type: unclassified calls, and classified ones like
        // AddSingleton(typeof(Foo)) that leave ServiceType and ImplType null. A resolved registration names exactly
        // one type, so its text must not also vouch for a same-named type in another namespace.
        var rawCalls = di.Registrations.Where(e => e.ServiceType is null && e.ImplType is null)
            .Concat(di.Unclassified)
            .Select(e => e.RawCall)
            .ToArray();
        return new RegistrationLookup(names, rawCalls);
    }

    public bool Covers(INamedTypeSymbol type) => Covers(type.ToDisplayString(), type.Name);

    /// <param name="displayName">Fully qualified name, e.g. <c>Ns.Repo&lt;T&gt;</c>.</param>
    /// <param name="simpleName">The type's name without namespace or type arguments, e.g. <c>Repo</c>.</param>
    public bool Covers(string displayName, string simpleName)
    {
        if (_names.Contains(RoslynHelpers.GenericDefinitionName(displayName))) return true;
        // Calls whose types the index couldn't resolve (AddMyThing<Foo>(), TryAddSingleton<Foo>(),
        // AddSingleton(typeof(Foo))) survive only as source text, so the simple name is all there is to match on.
        return _rawCalls.Any(c => MentionsIdentifier(c, simpleName));
    }

    /// <summary>
    /// Substring matching is wrong here: "CodeGenerationService" occurs inside every mention of
    /// "ICodeGenerationService", so a registered interface would silently exonerate the dead
    /// class named after it — exactly the case TOOL-006 exists to catch. Match whole identifiers.
    /// </summary>
    private static bool MentionsIdentifier(string text, string name)
    {
        for (var i = text.IndexOf(name, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(name, i + name.Length, StringComparison.Ordinal))
        {
            var startsWord = i == 0 || !IsIdentifierChar(text[i - 1]);
            var end = i + name.Length;
            var endsWord = end >= text.Length || !IsIdentifierChar(text[end]);
            if (startsWord && endsWord) return true;
        }
        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
