using System.Threading.Tasks;

namespace SshDeviceToolkit
{
    public interface IDeviceControlService
    {
        Task<string> IndexRestartAsync(string serialNumber, string clientId, string? ipOverride = null);
        Task<string> RebootAsync(string serialNumber);

        // Runner relies on these:
        Task<(string Ip, string WanMacRaw)?> GetDeviceDetailsFromDmpAsync(string serialNumber);
        Task<string> StopNgacsAsync(string serialNumber, string ipAddress, int port, string username, string password);
        Task<string> StartNgacsAsync(string serialNumber, string ipAddress, int port, string username, string password);
        Task<string> PingDeviceAsync(string ipAddress);
        Task<OnlineCheckResult> CheckIfOnlineAsync(string ipAddress, int port, string username, string password);
        Task<DeviceDetails?> GetDeviceDetailsFromDmp2Async(string serialNumber);

    }
}
