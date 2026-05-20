using System;

namespace TestLib;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class MyMarkerAttribute : Attribute { }

[MyMarker]
public class MarkedType
{
    [MyMarker]
    public void MarkedMethod() { }
}

internal sealed class UnreferencedInternalType
{
    public int Value { get; set; }
}
