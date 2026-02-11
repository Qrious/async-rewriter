using System;
namespace TestNamespace
{
    public class TestClass
    {
        public void OuterMethod()
        {
            Func<int, int, int> add = (a, b) => { return a + b; };
        }
    }
}
