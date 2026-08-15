namespace TestWeb;

/// <summary>
/// Takes a registered service type as a plain method parameter, not a constructor.
/// find_registrations must NOT list this as a DI consumer (TOOL-002).
/// </summary>
public static class FooHelper
{
    public static void Use(IFoo foo) { }
}
