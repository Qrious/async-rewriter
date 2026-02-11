namespace TestNamespace
{
    public class BaseClass
    {
        public virtual void DoWork() { }
    }

    public class DerivedClass : BaseClass
    {
        public override void DoWork() { }
    }
}
