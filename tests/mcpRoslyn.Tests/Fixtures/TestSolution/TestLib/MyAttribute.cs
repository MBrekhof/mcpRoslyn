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
