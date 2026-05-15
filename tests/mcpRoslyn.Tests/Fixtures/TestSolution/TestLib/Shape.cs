namespace TestLib;

public abstract class Shape
{
    public abstract double Area();
}

public class Circle(double radius) : Shape
{
    public override double Area() => Math.PI * radius * radius;
}

public class Square(double side) : Shape
{
    public override double Area() => side * side;
}
