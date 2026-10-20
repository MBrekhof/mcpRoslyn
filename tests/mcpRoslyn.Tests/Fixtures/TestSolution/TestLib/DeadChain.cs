namespace TestLib;

// TOOL-013 fixture. DeadEntry is referenced by nothing; OnlyFromDead is called only by DeadEntry, so it is dead too;
// Shared is also called by the public Live, which is never a candidate, so it stays alive.
public static partial class DeadChain
{
    public static int Live() => Shared() + Ping(0) + (typeof(ExplicitContractHolder) is not null ? 0 : 1);

    public static int Ping(int n) => n <= 0 ? 0 : Pong(n - 1);

    public static int Pong(int n) => n <= 0 ? 0 : Ping(n - 1);

    // The runtime runs a static constructor; nothing in source references it, and what it calls is not dead.
    static DeadChain() => RunsInStaticConstructor();

    private static void RunsInStaticConstructor() { }

    private static int DeadEntry() => OnlyFromDead() + Shared();

    private static int OnlyFromDead() => 7;

    private static int Shared() => 1;

    // An unused field is dead, and so is the type named only in its declaration.
    private static FieldOnlyType? UnusedField;

    // An unused field's initializer still runs, so what it calls is not dead.
    private static readonly int UnusedInitialized = RunsDuringTypeInit();

    private static int RunsDuringTypeInit() => 5;

    // An uncalled partial method is dead; the helper called only from its implementation part is dead too.
    private static partial int DeadPartial();

    private static partial int DeadPartial() => OnlyFromDeadPartial();

    private static int OnlyFromDeadPartial() => 3;
}

public interface IExplicitContract
{
    int Value { get; }
    event System.EventHandler? Fired;
}

// TOOL-015: explicit interface implementations have DeclaredAccessibility == Private and a qualified Name,
// so without an explicit-implementation check they were reported as high-confidence dead code alongside
// any private helpers they call.
public sealed class ExplicitContractHolder : System.IDisposable, IExplicitContract
{
    void System.IDisposable.Dispose() => RunsInExplicitDispose();
    int IExplicitContract.Value => RunsInExplicitProperty();
    event System.EventHandler? IExplicitContract.Fired
    {
        add => RunsInExplicitEvent();
        remove => RunsInExplicitEvent();
    }

    private static void RunsInExplicitDispose() { }
    private static int RunsInExplicitProperty() => 1;
    private static void RunsInExplicitEvent() { }
}

internal sealed class FieldOnlyType
{
}
