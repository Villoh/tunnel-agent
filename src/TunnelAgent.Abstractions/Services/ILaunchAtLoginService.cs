using System.Threading.Tasks;

namespace TunnelAgent.Services;

public interface ILaunchAtLoginService
{
    bool IsSupported { get; }
    Task<bool> GetEnabledAsync();
    Task SetEnabledAsync(bool enabled);
}
