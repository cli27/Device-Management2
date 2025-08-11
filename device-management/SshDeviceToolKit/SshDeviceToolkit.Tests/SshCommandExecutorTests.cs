using System.Threading.Tasks;
using Xunit;
using Moq;
using SshDeviceToolkit;

namespace SshDeviceToolkit.Tests
{
    public class SshCommandExecutorTests
    {
        [Fact]
        public async Task ExecuteCommandAsync_ReturnsExpectedResult()
        {
            var mockClient = new Mock<ISshClientWrapper>();
            mockClient.Setup(c => c.Connect());
            mockClient.Setup(c => c.RunCommand("uptime")).Returns("Up 3 days");
            mockClient.Setup(c => c.Disconnect());

            var executor = new SshCommandExecutor(ip => mockClient.Object);
            var result = await executor.ExecuteCommandAsync("10.0.0.1", "uptime");

            Assert.Equal("Up 3 days", result);
        }

        [Fact]
        public async Task GetCommandStatusAsync_ReturnsExpectedStatus()
        {
            var mockClient = new Mock<ISshClientWrapper>();
            mockClient.Setup(c => c.Connect());
            mockClient.Setup(c => c.RunCommand("status")).Returns("Completed");
            mockClient.Setup(c => c.Disconnect());

            var executor = new SshCommandExecutor(ip => mockClient.Object);
            var result = await executor.GetCommandStatusAsync("10.0.0.1", "status");

            Assert.Equal("Completed", result);
        }
    }
}
