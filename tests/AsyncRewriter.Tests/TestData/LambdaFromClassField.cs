using System;
using System.Collections.Generic;
using System.Linq;
namespace TestNamespace
{
    public class TestClass
    {
        private readonly Func<int, bool> _filter = (n) => n > 5;

        public List<int> FilterNumbers(List<int> numbers)
        {
            return numbers.Where(_filter).ToList();
        }
    }
}
