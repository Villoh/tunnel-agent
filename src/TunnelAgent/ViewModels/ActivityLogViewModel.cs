namespace TunnelAgent.ViewModels;

public sealed class ActivityLogViewModel
{
    public ActivityLogViewModel(string method, string path, string agent, string provider, string model, string status, string elapsed, string when)
    {
        Method = method;
        Path = path;
        Agent = agent;
        Provider = provider;
        Model = model;
        Status = status;
        Elapsed = elapsed;
        When = when;
    }

    public string Method { get; }
    public string Path { get; }
    public string Agent { get; }
    public string Provider { get; }
    public string Model { get; }
    public string Status { get; }
    public string Elapsed { get; }
    public string When { get; }
}
