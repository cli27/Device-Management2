using System;

namespace SshDeviceToolkit
{
    public interface ISshClientWrapper : IDisposable
    {
        void Connect();
        void Disconnect();
        bool IsConnected { get; }
        string RunCommand(string command);
    }
}
