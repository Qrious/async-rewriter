using System;
using LibraryNamespace;
namespace ConsumerNamespace
{
    public class TestClass
    {
        public void Test()
        {
            var x = new LazyExecutor<int>(() => 3).Execute();
        }
    }
}
