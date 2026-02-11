using System;
using System.Linq.Expressions;
namespace TestNamespace
{
    public interface IService
    {
        int GetValue();
    }

    public class TestClass
    {
        private readonly IService _service;

        public TestClass(IService service)
        {
            _service = service;
        }

        // This simulates A.CallTo(() => service.Method()) from FakeItEasy
        // or mock.Setup(x => x.Method()) from Moq.
        // The lambda is an Expression<T>, so calls inside should NOT be in the call graph.
        public void TestMethod()
        {
            Expression<Func<int>> expr = () => _service.GetValue();
            SomeFrameworkMethod(expr);
        }

        // A regular lambda call that SHOULD be in the call graph
        public void RegularLambdaMethod()
        {
            Func<int> func = () => _service.GetValue();
        }

        private void SomeFrameworkMethod(Expression<Func<int>> expression) { }
    }
}
