namespace Shared;

// IDX-005 fixture: linked into TestWeb. Only TestWeb's copy (TESTWEB defined) declares an extension
// method — which is what makes a static class framework-reached — so the declaration is not dead.
public static class LinkedExtensions
{
#if TESTWEB
    public static int Twice(this int value) => value * 2;
#endif
}
