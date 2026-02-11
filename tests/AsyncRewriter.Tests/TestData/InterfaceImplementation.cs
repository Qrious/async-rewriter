namespace TestNamespace
{
    public interface IService
    {
        void DoWork();
        int Calculate(int x);
    }

    public class ServiceImpl : IService
    {
        public void DoWork() { }
        public int Calculate(int x) { return x * 2; }
    }
}
