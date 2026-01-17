using System;

namespace ComplexHierarchy;

public class EmailMessageHandler : BaseMessageHandler
{
    protected override void ProcessMessage(string message)
    {
        SendEmail(message);
    }

    private void SendEmail(string message)
    {
        // Would be async in real scenario
        Console.WriteLine($"Sending email: {message}");
    }
}
