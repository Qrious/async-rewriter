using System;

namespace DeepCallChain;

public class DeepChain
{
    public void Level1()
    {
        Console.WriteLine("Level 1");
        Level2();
    }

    private void Level2()
    {
        Console.WriteLine("Level 2");
        Level3();
    }

    private void Level3()
    {
        Console.WriteLine("Level 3");
        Level4();
    }

    private void Level4()
    {
        Console.WriteLine("Level 4");
        Level5();
    }

    private void Level5()
    {
        Console.WriteLine("Level 5");
        Level6();
    }

    private void Level6()
    {
        Console.WriteLine("Level 6");
        Level7();
    }

    private void Level7()
    {
        Console.WriteLine("Level 7");
        Level8Async();
    }

    private void Level8Async()
    {
        // This would be the root async method
        Console.WriteLine("Level 8 - Async operation");
    }
}
