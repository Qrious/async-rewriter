namespace TestNamespace
{
    public class GrandParent
    {
        public virtual void Process() { }
    }

    public class Parent : GrandParent
    {
        public override void Process() { }
    }

    public class Child : Parent
    {
        public override void Process() { }
    }
}
