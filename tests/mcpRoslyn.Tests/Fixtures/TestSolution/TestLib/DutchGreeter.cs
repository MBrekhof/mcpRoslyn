namespace TestLib;

public class DutchGreeter : IGreeter
{
    public string Greet(string name) => $"Hallo, {name}!";
}
