using System.Collections.Generic;

namespace ComplexHierarchy;

public class MessageProcessor
{
    private readonly List<IMessageHandler> _handlers;

    public MessageProcessor(List<IMessageHandler> handlers)
    {
        _handlers = handlers;
    }

    public void ProcessAllMessages(List<string> messages)
    {
        foreach (var message in messages)
        {
            ProcessSingleMessage(message);
        }
    }

    private void ProcessSingleMessage(string message)
    {
        foreach (var handler in _handlers)
        {
            handler.HandleMessage(message);
        }
    }
}
