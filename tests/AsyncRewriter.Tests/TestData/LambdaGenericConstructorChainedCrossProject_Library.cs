using System;
namespace LibraryNamespace
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
}
