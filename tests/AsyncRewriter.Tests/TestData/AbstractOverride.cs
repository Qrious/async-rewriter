namespace TestNamespace
{
    public abstract class AbstractBase
    {
        public abstract int Calculate(int x);
    }

    public class ConcreteClass : AbstractBase
    {
        public override int Calculate(int x) { return x * 2; }
    }
}
