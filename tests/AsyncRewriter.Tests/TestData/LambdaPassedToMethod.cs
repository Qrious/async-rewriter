using System;
namespace TestNamespace
{
    public class Executor
    {
        public void Run(Action action) { action(); }
    }

    public class TestClass
    {
        public void OuterMethod()
        {
            var executor = new Executor();
            executor.Run(() => { InnerMethod(); });
        }
        public void InnerMethod() { }
    }
}
