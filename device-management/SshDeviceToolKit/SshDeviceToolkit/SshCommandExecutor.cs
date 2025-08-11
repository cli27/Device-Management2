using System;
using System.Threading.Tasks;

namespace SshDeviceToolkit
{
    public class SshCommandExecutor : ISshCommandExecutor
    {
        private readonly Func<string, ISshClientWrapper> _clientFactory;

        public SshCommandExecutor(Func<string, ISshClientWrapper> clientFactory)
        {
            _clientFactory = clientFactory;
        }

        public async Task<string> ExecuteCommandAsync(string deviceIp, string command)
        {
            using var client = _clientFactory(deviceIp);
            client.Connect();
            var result = await Task.Run(() => client.RunCommand(command));
            client.Disconnect();
            return result;
        }

        public async Task<string> GetCommandStatusAsync(string deviceIp, string statusCommand)
        {
            using var client = _clientFactory(deviceIp);
            client.Connect();
            var result = await Task.Run(() => client.RunCommand(statusCommand));
            client.Disconnect();
            return result;
        }
    }
}
