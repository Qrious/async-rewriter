using System;

namespace ComplexHierarchy;

public abstract class BaseMessageHandler : IMessageHandler
{
    public virtual void HandleMessage(string message)
    {
        ValidateMessage(message);
        ProcessMessage(message);
        LogMessage(message);
    }

    protected void ValidateMessage(string message)
    {
        Console.WriteLine($"Validating message: {message}");
    }

    protected abstract void ProcessMessage(string message);

    protected void LogMessage(string message)
    {
        WriteToLog(message);
    }

    private void WriteToLog(string message)
    {
        Console.WriteLine($"Logging: {message}");
    }
}
