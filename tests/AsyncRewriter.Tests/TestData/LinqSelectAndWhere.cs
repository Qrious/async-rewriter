using System;
using System.Collections.Generic;
using System.Linq;
namespace TestNamespace
{
    public class TestClass
    {
        public List<int> FilterAndProject(List<int> numbers)
        {
            Func<int, bool> where = (n) => n > 5;
            return numbers
                .Where(where)
                .Select(n => n * 2)
                .ToList();
        }
    }
}
