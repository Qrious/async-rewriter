using System;

namespace ComplexHierarchy;

public class SmsMessageHandler : BaseMessageHandler
{
    protected override void ProcessMessage(string message)
    {
        SendSms(message);
    }

    private void SendSms(string message)
    {
        // Would be async in real scenario
        Console.WriteLine($"Sending SMS: {message}");
    }
}
