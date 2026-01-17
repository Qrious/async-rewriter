namespace InterfaceImplementation;

public class Controller
{
    private readonly IService _service;

    public Controller(IService service)
    {
        _service = service;
    }

    public void HandleRequest()
    {
        var data = _service.GetData();
        _service.ProcessData(data);
    }
}
