using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TunnelAgent.Services;

public interface ICredentialStore
{
    void Save(string providerId, string secret);
    string? Load(string providerId);
    void Remove(string providerId);
}

/// <summary>
/// Windows-only DPAPI-backed credential store (per-user scope).
/// Files live under %AppData%\TunnelAgent\credentials\{providerId}.dat
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TunnelAgent", "credentials");

    public DpapiCredentialStore() => Directory.CreateDirectory(_root);

    public void Save(string providerId, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var enc = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(_root, providerId + ".dat"), enc);
    }

    public string? Load(string providerId)
    {
        var path = Path.Combine(_root, providerId + ".dat");
        if (!File.Exists(path)) return null;
        var enc = File.ReadAllBytes(path);
        var dec = ProtectedData.Unprotect(enc, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(dec);
    }

    public void Remove(string providerId)
    {
        var path = Path.Combine(_root, providerId + ".dat");
        if (File.Exists(path)) File.Delete(path);
    }
}
