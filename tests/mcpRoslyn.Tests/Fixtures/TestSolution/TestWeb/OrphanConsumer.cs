namespace TestWeb;

// TOOL-013 fixture: IBaz is registered in Program.cs, and this unreferenced class is its only constructor consumer.
public sealed class OrphanConsumer
{
    public OrphanConsumer(IBaz baz) { }
}
