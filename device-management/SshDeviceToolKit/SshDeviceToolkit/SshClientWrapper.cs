using Renci.SshNet;

namespace SshDeviceToolkit
{
    public class SshClientWrapper : ISshClientWrapper
    {
        private readonly SshClient _sshClient;

        public SshClientWrapper(string host, int port, string username, string password)
        {
            _sshClient = new SshClient(host, port, username, password);
        }

        public void Connect() => _sshClient.Connect();
        public void Disconnect() => _sshClient.Disconnect();
        public bool IsConnected => _sshClient.IsConnected;

        public string RunCommand(string command)
        {
            var result = _sshClient.RunCommand(command);
            return result.Result;
        }

        public void Dispose() => _sshClient.Dispose();
    }
}
