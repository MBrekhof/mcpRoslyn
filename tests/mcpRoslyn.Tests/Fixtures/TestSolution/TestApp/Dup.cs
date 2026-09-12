namespace Shared;

// IDX-005 fixture: TestWeb declares a type with the same fully-qualified name. Neither project
// references the other, so these are two distinct symbols sharing the id "T:Shared.Dup".
public class Dup
{
}
