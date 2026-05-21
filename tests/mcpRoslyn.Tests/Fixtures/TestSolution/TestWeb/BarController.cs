namespace TestWeb;

public sealed class BarController
{
    private readonly IFoo _foo;
    public BarController(IFoo foo) => _foo = foo;
}
