using System;
namespace TestNamespace
{
    public class TestClass
    {
        public void OuterMethod()
        {
            void LocalFunc() { Console.WriteLine("hello"); }
            LocalFunc();
        }
    }
}