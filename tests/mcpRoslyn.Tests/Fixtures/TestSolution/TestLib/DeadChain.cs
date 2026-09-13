namespace TestLib;

// TOOL-013 fixture. DeadEntry is referenced by nothing; OnlyFromDead is called only by DeadEntry, so it is dead too;
// Shared is also called by the public Live, which is never a candidate, so it stays alive.
public static partial class DeadChain
{
    public static int Live() => Shared();

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

internal sealed class FieldOnlyType
{
}
