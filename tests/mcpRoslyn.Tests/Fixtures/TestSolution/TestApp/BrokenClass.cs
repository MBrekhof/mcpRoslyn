namespace TestApp;

public class BrokenClass
{
    public int Add(int a, int b) => a + ; // deliberate syntax error
}
