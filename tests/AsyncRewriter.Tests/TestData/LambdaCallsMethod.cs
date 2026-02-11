using System;
namespace TestNamespace
{
    public class TestClass
    {
        public void OuterMethod()
        {
            Action action = () => { InnerMethod(); };
        }
        public void InnerMethod() { }
    }
}
