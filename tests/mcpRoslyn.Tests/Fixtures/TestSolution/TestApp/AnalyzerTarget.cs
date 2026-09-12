namespace TestApp;

// DIAG-001 fixture. TestApp/.editorconfig raises CA1822 (member can be static) to warning, so it
// is reported only when analyzers run. Thrice is pragma-suppressed and must never be reported.
public class AnalyzerTarget
{
    public int Twice(int x) => x * 2;

#pragma warning disable CA1822
    public int Thrice(int x) => x * 3;
#pragma warning restore CA1822
}
