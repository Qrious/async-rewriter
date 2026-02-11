namespace TestNamespace
{
    public class TestClass
    {
        public void OuterMethod()
        {
            void Middle()
            {
                int Inner() { return 1; }
            }
        }
    }
}