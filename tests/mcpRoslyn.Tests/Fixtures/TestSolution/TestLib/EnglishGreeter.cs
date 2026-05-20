namespace TestLib;

public class EnglishGreeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name.Trim()}!";

    /// <summary>Empty method body — used by find_callees "no invocations" test.</summary>
    public void GreetEmpty() { }
}
