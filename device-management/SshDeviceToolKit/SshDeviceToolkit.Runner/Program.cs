using SshDeviceToolkit;

var sshFactory = (string ip, int port, string username, string password) =>
    new SshClientWrapper(ip, port, username, password);

var controller = new DeviceControlService(sshFactory);
const int port = 8822;
const string username = "superadmin";

// Prompt for inputs on launch
Console.WriteLine("🔧 Enter the device Serial Number:");
string serialNumber = Console.ReadLine()?.Trim() ?? "";

Console.WriteLine("🔐 Enter the Client ID:");
string clientId = Console.ReadLine()?.Trim() ?? "";

// Main loop
while (true)
{
    Console.WriteLine("\n🛠️ Welcome to the SSH Device Toolkit");
    Console.WriteLine("Choose an action:");
    Console.WriteLine("1. restart   - Perform index restart via SSH");
    Console.WriteLine("2. reboot    - Perform reboot via API");
    Console.WriteLine("3. both      - Do restart then reboot");
    Console.WriteLine("4. stop      - SSH ngacs stop only");
    Console.WriteLine("5. start     - SSH ngacs start only");
    Console.WriteLine("6. check     - Ping the device to check if online");
    Console.WriteLine("7. getinfo   - Get current IP & MAC from DMP");
    Console.WriteLine("8. exit      - Exit the tool");
    Console.Write("Enter your choice: ");

    var choice = Console.ReadLine()?.Trim().ToLower();

    switch (choice)
    {
        case "restart":
            Console.WriteLine("🔄 Triggering index restart...");
            Console.WriteLine(await controller.IndexRestartAsync(serialNumber, clientId));
            break;

        case "reboot":
            Console.WriteLine("♻️ Triggering reboot via API...");
            Console.WriteLine(await controller.RebootAsync(serialNumber));
            break;

        case "both":
            Console.WriteLine("🔄 Triggering index restart...");
            Console.WriteLine(await controller.IndexRestartAsync(serialNumber, clientId));
            Console.WriteLine("♻️ Triggering reboot via API...");
            Console.WriteLine(await controller.RebootAsync(serialNumber));
            break;

        case "stop":
            Console.WriteLine("🛑 Triggering ngacs stop...");
            {
                var info = await controller.GetDeviceDetailsFromDmpAsync(serialNumber);
                if (info == null || string.IsNullOrWhiteSpace(info.Value.WanMacRaw))
                {
                    Console.WriteLine("❌ Could not get device IP/WAN MAC. Use 'getinfo' to debug.");
                    break;
                }
                var clean = DeviceControlService.CleanWanMacForPassword(info.Value.WanMacRaw);
                var password = DeviceControlService.GeneratePassword(clientId, clean);
                Console.WriteLine(await controller.StopNgacsAsync(serialNumber, info.Value.Ip, port, username, password));
            }
            break;

        case "start":
            Console.WriteLine("▶️ Triggering ngacs start...");
            {
                var info = await controller.GetDeviceDetailsFromDmpAsync(serialNumber);
                if (info == null || string.IsNullOrWhiteSpace(info.Value.WanMacRaw))
                {
                    Console.WriteLine("❌ Could not get device IP/WAN MAC. Use 'getinfo' to debug.");
                    break;
                }
                var clean = DeviceControlService.CleanWanMacForPassword(info.Value.WanMacRaw);
                var password = DeviceControlService.GeneratePassword(clientId, clean);
                Console.WriteLine(await controller.StartNgacsAsync(serialNumber, info.Value.Ip, port, username, password));
            }
            break;

        case "check":
            Console.WriteLine("📡 Checking if device is online...");
            {
                var info = await controller.GetDeviceDetailsFromDmpAsync(serialNumber);
                if (info == null)
                {
                    Console.WriteLine("❌ Could not get device info.");
                    break;
                }
                var clean = DeviceControlService.CleanWanMacForPassword(info.Value.WanMacRaw ?? "");
                var password = DeviceControlService.GeneratePassword(clientId, clean);
                var result = await controller.CheckIfOnlineAsync(info.Value.Ip, port, username, password);
                Console.WriteLine($"Success: {result.Success}");
                Console.WriteLine($"Online: {result.Online}");
                Console.WriteLine($"DMPOnline: {result.DmpOnline}");
            }
            break;

        case "getinfo":
            Console.WriteLine("🌐 Getting current IP & WAN MAC via DMP parameter endpoint...");
            {
                var info = await controller.GetDeviceDetailsFromDmpAsync(serialNumber);
                if (info == null)
                {
                    Console.WriteLine("❌ Could not retrieve any network info.");
                }
                else
                {
                    var clean = DeviceControlService.CleanWanMacForPassword(info.Value.WanMacRaw ?? "");
                    Console.WriteLine($"✅ Candidate IP from DMP: {info.Value.Ip}");
                    Console.WriteLine($"✅ WAN MAC (raw) from DMP: {(string.IsNullOrWhiteSpace(info.Value.WanMacRaw) ? "(missing)" : info.Value.WanMacRaw)}");
                    Console.WriteLine($"✅ WAN MAC (clean 12-hex): {clean}");
                    Console.WriteLine("Note: Password = ClientID + '!' + CLEAN WAN MAC.");
                }
            }
            break;

        case "exit":
            Console.WriteLine("👋 Exiting... Goodbye!");
            return;

        default:
            Console.WriteLine("❌ Invalid option. Please try again.");
            break;
    }

    Console.WriteLine("\nPress any key to continue...");
    Console.ReadKey();
    Console.Clear();
}
