namespace TestLib;

public class EnglishGreeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}!";
}
