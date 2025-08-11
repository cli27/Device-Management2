using System.Threading.Tasks;

namespace SshDeviceToolkit
{
    public interface ISshCommandExecutor
    {
        Task<string> ExecuteCommandAsync(string deviceIp, string command);
        Task<string> GetCommandStatusAsync(string deviceIp, string statusCommand);
    }
}
