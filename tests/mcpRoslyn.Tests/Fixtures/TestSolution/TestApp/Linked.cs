namespace Shared;

// IDX-005 fixture: this one file is compiled into TestApp and, as a link, into TestWeb, so the same
// path declares Shared.Linked in two assemblies — two symbols, one id, one file.
public class Linked
{
}
