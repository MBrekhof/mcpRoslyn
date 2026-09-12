namespace Shared;

// IDX-005 fixture: linked into TestWeb, which defines TESTWEB — so this one declaration is public
// there and internal in TestApp. Its most exposed copy is public, so it is not dead-code material.
#if TESTWEB
public
#else
internal
#endif
class LinkedConditional
{
}
