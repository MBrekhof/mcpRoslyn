namespace TestTests;

public sealed class EnglishGreeterTests
{
    public void Greet_returns_hello()
    {
        var greeter = new TestLib.EnglishGreeter();
        var _ = greeter.Greet("World");
    }
}
