namespace TestNamespace
{
    public class TestClass
    {
        public void OuterMethod()
        {
            int LocalFunc() { return 42; }
            LocalFunc();
        }
    }
}