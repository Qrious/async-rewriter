using System;
namespace TestNamespace
{
    public class Executor
    {
        private readonly Action _action;

        public Executor(Action action)
        {
            _action = action;
        }

        public void Execute()
        {
            _action();
        }
    }

    public class TestClass
    {
        public void Setup()
        {
            var executor = new Executor(() => { DoWork(); });
            executor.Execute();
        }

        public void DoWork() { }
    }
}
