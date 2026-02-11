using System;
namespace TestNamespace
{
    public class TestClass
    {
        public void OuterMethod()
        {
            Action action = () => { MiddleMethod(); };
        }
        public void MiddleMethod()
        {
            Action nested = () => { InnerMethod(); };
        }
        public void InnerMethod() { }
    }
}
