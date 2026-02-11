using System;
namespace TestNamespace
{
    public class LazyExecutor<T>
    {
        private Func<T> _func;

        public LazyExecutor(Func<T> func)
        {
            _func = func;
        }

        public T Execute()
        {
            return _func();
        }
    }

    public class TestClass
    {
        public void Test()
        {
            var x = new LazyExecutor<int>(() => 3).Execute();
        }
    }
}
