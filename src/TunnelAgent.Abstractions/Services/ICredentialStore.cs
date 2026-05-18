namespace TunnelAgent.Services;

public interface ICredentialStore
{
    void Save(string providerId, string secret);
    string? Load(string providerId);
    void Remove(string providerId);
}
