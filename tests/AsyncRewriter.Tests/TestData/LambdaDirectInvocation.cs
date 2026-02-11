using System;
namespace TestNamespace
{
    public class TestClass
    {
        public void OuterMethod()
        {
            Action action = () => { InnerMethod(); };
            action();
        }
        public void InnerMethod() { }
    }
}
