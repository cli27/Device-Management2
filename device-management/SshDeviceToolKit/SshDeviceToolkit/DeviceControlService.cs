using System.Text;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SshDeviceToolkit
{
    public class DeviceControlService : IDeviceControlService
    {
        private readonly HttpClient _httpClient;
        private readonly Func<string, int, string, string, ISshClientWrapper> _sshFactory;
        private const int DefaultPort = 8822;
        private const int FallbackPort = 22;
        private const string DefaultUsername = "superadmin";

        public DeviceControlService(Func<string, int, string, string, ISshClientWrapper> sshFactory)
        {
            _sshFactory = sshFactory;
            _httpClient = new HttpClient();
        }

        // Public helpers used by Runner
        public static string CleanWanMacForPassword(string raw)
        {
            var hex = Regex.Replace(raw ?? "", "[^0-9A-Fa-f]", "");
            if (hex.Length < 12) return hex.ToUpperInvariant();
            return hex.Substring(0, 12).ToUpperInvariant();
        }

        public static string GeneratePassword(string clientId, string wanMacClean12Hex)
        => clientId.Trim() + "!" + wanMacClean12Hex;


        public async Task<string> IndexRestartAsync(string serialNumber, string clientId, string? ipOverride = null)
        {
            try
            {
                var deviceInfo = await GetDeviceDetailsFromDmpAsync(serialNumber);
                if (deviceInfo == null)
                    return $"❌ Failed to retrieve device info for {serialNumber} (IP/WAN MAC not found). Choose 'getinfo' to debug.";

                var cleanWanMac = CleanWanMacForPassword(deviceInfo.Value.WanMacRaw);
                if (string.IsNullOrWhiteSpace(cleanWanMac) || cleanWanMac.Length != 12)
                    return $"❌ WAN MAC missing/invalid from DMP response, cannot compute SSH password (expected 12 hex). Got: '{deviceInfo.Value.WanMacRaw}'";

                string ipCandidate = ipOverride ?? deviceInfo.Value.Ip;
                string password = GeneratePassword(clientId, cleanWanMac);

                var (reachable, ip, port) = await PickReachableIpAndPort(ipCandidate);
                if (!reachable)
                {
                    return $"❌ Could not reach device for SSH.\n" +
                           $"- Candidate IP from DMP: {ipCandidate}\n" +
                           $"- Tried ports: {DefaultPort}, {FallbackPort}\n" +
                           $"Tip: If this is a mobile/private IP, you may need the Ethernet/LAN IP or VPN.\n";
                }

                var log = $"Connecting to {serialNumber} at {ip}:{port} as {DefaultUsername}\n";
                log += $"Using WAN MAC (clean): {cleanWanMac}\n";

                using var sshClient = _sshFactory(ip, port, DefaultUsername, password);
                sshClient.Connect();

                if (!sshClient.IsConnected)
                    return $"❌ Failed to connect to {serialNumber} ({ip}:{port}).";

                var swStop = Stopwatch.StartNew();
                var stopResult = await Task.Run(() => sshClient.RunCommand("/etc/init.d/ngacsclient stop"));
                swStop.Stop();
                log += $"\n🔚 ngacs stop completed in {swStop.ElapsedMilliseconds} ms\nOutput:\n{stopResult}\n";

                var swStart = Stopwatch.StartNew();
                var startResult = await Task.Run(() => sshClient.RunCommand("/etc/init.d/ngacsclient start"));
                swStart.Stop();
                log += $"\n▶️ ngacs start completed in {swStart.ElapsedMilliseconds} ms\nOutput:\n{startResult}\n";

                var statusResult = await Task.Run(() => sshClient.RunCommand("ps | grep ngacs"));
                log += $"\n📊 ngacs status (via ps):\n{statusResult}\n";

                return $"✅ Index restart completed for {serialNumber}.\n{log}";
            }
            catch (Exception ex)
            {
                return $"❌ Index restart failed for {serialNumber}.\nError: {ex.Message}";
            }
        }

        /// <summary>
        /// Returns Ip + WanMacRaw (untrimmed). Caller must use CleanWanMacForPassword before building password.
        /// NOTE: Tuple element *names* matter for callers using named access.
        /// </summary>
        public async Task<(string Ip, string WanMacRaw)?> GetDeviceDetailsFromDmpAsync(string serialNumber)
        {
            string? token = await GetAuthTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.WriteLine("❌ Could not obtain token for DMP parameter call.");
                return null;
            }

            var pathsToTry = new[]
            {
                "+Status.Network",
                "+Status.Network.EthernetWAN",
                "+Status.Network.Mobile",
                "+Status.Network.LAN"
            };

            string? foundIp = null;
            string? foundWan = null;

            foreach (var path in pathsToTry)
            {
                var (ok, root) = await CallParameterPath(token, serialNumber, path);
                if (!ok || root.ValueKind == JsonValueKind.Undefined) continue;

                foundWan ??= FindMacAddress(root);
                foundIp  ??= FindIPv4Address(root);

                if (!string.IsNullOrWhiteSpace(foundIp) && !string.IsNullOrWhiteSpace(foundWan))
                    break;
            }

            if (!string.IsNullOrWhiteSpace(foundIp) && !string.IsNullOrWhiteSpace(foundWan))
                return (foundIp!, foundWan!);

            Console.WriteLine($"❌ Could not extract IP/WAN MAC from DMP data. IP:'{foundIp ?? ""}', WAN:'{foundWan ?? ""}'");
            return null;
        }

        private async Task<(bool ok, JsonElement root)> CallParameterPath(string token, string serialNumber, string path)
        {
            var dataPayload = JsonSerializer.Serialize(new { data = new { path } });
            var url = $"https://api.dataremote.com/ngacs/cpe/{serialNumber}/parameter?timeout=60&data={Uri.EscapeDataString(dataPayload)}";

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _httpClient.SendAsync(req);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ Error calling DMP parameter endpoint ({path}): {response.StatusCode} - {content}");
                    return (false, default);
                }

                using var doc = JsonDocument.Parse(content);
                return (true, doc.RootElement.Clone());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception calling DMP parameter endpoint ({path}): {ex.Message}");
                return (false, default);
            }
        }

        private async Task<string?> GetAuthTokenAsync()
        {
            var loginUrl = "https://api.dataremote.com/auth/login";

            var content = new MultipartFormDataContent
            {
                { new StringContent("dagster@mettel.net"), "email" },
                { new StringContent("9F1gB9BvMo9McoN3PWC9gzT1G"), "password" }
            };

            var response = await _httpClient.PostAsync(loginUrl, content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Login failed: {response.StatusCode} - {responseJson}");
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("authorization_token", out var tokenElement))
                    return tokenElement.GetString();

                Console.WriteLine("❌ Token not found in response.");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to parse token. Error: {ex.Message}");
                return null;
            }
        }

        // ===== Robust recursive JSON scans =====

        private static string? FindIPv4Address(JsonElement root)
        {
            var eth = FindFirstValueForKey(root, "EthernetWAN");
            var ethIp = FindValueUnderKey(eth, "IPv4Address");
            if (LooksLikeIp(ethIp)) return StripPort(ethIp);

            var mob = FindFirstValueForKey(root, "Mobile");
            var mobIp = FindValueUnderKey(mob, "IPv4Address");
            if (LooksLikeIp(mobIp)) return StripPort(mobIp);

            var lan = FindFirstValueForKey(root, "LAN");
            var lanIp = FindValueUnderKey(lan, "IPv4Address");
            if (LooksLikeIp(lanIp)) return StripPort(lanIp);

            var anyIp = FindValueUnderKey(root, "IPv4Address");
            return LooksLikeIp(anyIp) ? StripPort(anyIp) : null;
        }

        private static string? FindMacAddress(JsonElement root)
        {
            var eth = FindFirstValueForKey(root, "EthernetWAN");
            var mac = FindValueUnderKey(eth, "MACAddress");
            if (LooksLikeMac(mac)) return mac;

            mac = FindValueUnderKey(root, "MACAddress");
            return LooksLikeMac(mac) ? mac : null;
        }

        private static JsonElement FindFirstValueForKey(JsonElement root, string keyName)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (string.Equals(prop.Name, keyName, StringComparison.OrdinalIgnoreCase))
                        return prop.Value;

                    var childHit = FindFirstValueForKey(prop.Value, keyName);
                    if (childHit.ValueKind != JsonValueKind.Undefined) return childHit;
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var childHit = FindFirstValueForKey(item, keyName);
                    if (childHit.ValueKind != JsonValueKind.Undefined) return childHit;
                }
            }
            return default;
        }

        private static string? FindValueUnderKey(JsonElement root, string keyName)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (string.Equals(prop.Name, keyName, StringComparison.OrdinalIgnoreCase))
                    {
                        var v = prop.Value;
                        if (v.ValueKind == JsonValueKind.Object)
                        {
                            if (v.TryGetProperty("value", out var ve) && ve.ValueKind == JsonValueKind.String)
                                return ve.GetString();
                        }
                        if (v.ValueKind == JsonValueKind.String)
                            return v.GetString();
                    }

                    var sub = FindValueUnderKey(prop.Value, keyName);
                    if (!string.IsNullOrWhiteSpace(sub)) return sub;
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var sub = FindValueUnderKey(item, keyName);
                    if (!string.IsNullOrWhiteSpace(sub)) return sub;
                }
            }
            return null;
        }

        // ===== Connectivity helpers =====

        private async Task<(bool reachable, string ip, int port)> PickReachableIpAndPort(string ipCandidate)
        {
            if (await IsReachable(ipCandidate, DefaultPort)) return (true, ipCandidate, DefaultPort);
            if (await IsReachable(ipCandidate, FallbackPort)) return (true, ipCandidate, FallbackPort);
            return (false, ipCandidate, DefaultPort);
        }

        private async Task<bool> IsReachable(string ip, int port)
        {
            try { var ping = new Ping(); _ = await ping.SendPingAsync(ip, 1500); } catch { /* ignore */ }
            return await IsTcpOpen(ip, port, 2000);
        }

        private static async Task<bool> IsTcpOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(timeoutMs);
                var finished = await Task.WhenAny(connectTask, timeoutTask);
                return finished == connectTask && client.Connected;
            }
            catch { return false; }
        }

        private static bool LooksLikeIp(string? value)
        {
            var candidate = StripPort(value);
            return !string.IsNullOrWhiteSpace(candidate) && System.Net.IPAddress.TryParse(candidate, out _);
        }

        private static string? StripPort(string? value)
        {
            var s = value?.Trim();
            if (string.IsNullOrEmpty(s)) return s;
            var idx = s.IndexOf(':');
            return idx > 0 ? s.Substring(0, idx) : s;
        }

        private static bool LooksLikeMac(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var macRegex = new Regex(@"^([0-9A-Fa-f]{2}([:\-]?)){5}[0-9A-Fa-f]{2}$");
            return macRegex.IsMatch(value.Trim());
        }

        // ---- Existing SSH ops ----

        public async Task<string> StopNgacsAsync(string serialNumber, string ipAddress, int port, string username, string password)
        {
            try
            {
                using var sshClient = _sshFactory(ipAddress, port, username, password);
                sshClient.Connect();

                if (!sshClient.IsConnected)
                    return $"❌ Failed to connect to {serialNumber} ({ipAddress}:{port})";

                var log = $"Connected to {serialNumber} ({ipAddress}:{port})\n";

                var stopwatch = Stopwatch.StartNew();
                var result = await Task.Run(() => sshClient.RunCommand("/etc/init.d/ngacsclient stop"));
                stopwatch.Stop();

                log += $"\n🔚 ngacs stop completed in {stopwatch.ElapsedMilliseconds} ms\nOutput:\n{result}\n";
                return $"✅ ngacs stop completed for {serialNumber}.\n{log}";
            }
            catch (Exception ex)
            {
                return $"❌ ngacs stop failed for {serialNumber}.\nError: {ex.Message}";
            }
        }

        public async Task<string> StartNgacsAsync(string serialNumber, string ipAddress, int port, string username, string password)
        {
            try
            {
                using var sshClient = _sshFactory(ipAddress, port, username, password);
                sshClient.Connect();

                if (!sshClient.IsConnected)
                    return $"❌ Failed to connect to {serialNumber} ({ipAddress}:{port})";

                var log = $"Connected to {serialNumber} ({ipAddress}:{port})\n";

                var stopwatch = Stopwatch.StartNew();
                var result = await Task.Run(() => sshClient.RunCommand("/etc/init.d/ngacsclient start"));
                stopwatch.Stop();

                log += $"\n▶️ ngacs start completed in {stopwatch.ElapsedMilliseconds} ms\nOutput:\n{result}\n";
                return $"✅ ngacs start completed for {serialNumber}.\n{log}";
            }
            catch (Exception ex)
            {
                return $"❌ ngacs start failed for {serialNumber}.\nError: {ex.Message}";
            }
        }

        public async Task<string> RebootAsync(string serialNumber)
        {
            var token = await GetAuthTokenAsync();

            if (string.IsNullOrEmpty(token))
                return $"❌ Failed to obtain authorization token.";

            var url = $"https://api.dataremote.com/ngacs/cpe/{serialNumber}/reboot";

            try
            {
                var stopwatch = Stopwatch.StartNew();
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var response = await _httpClient.SendAsync(request);
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    return $"❌ Reboot failed for {serialNumber}: {response.StatusCode} - {error}\n⏱️ Time: {stopwatch.ElapsedMilliseconds} ms";
                }

                return $"✅ Reboot triggered for {serialNumber}: {response.StatusCode}\n⏱️ Time: {stopwatch.ElapsedMilliseconds} ms";
            }
            catch (Exception ex)
            {
                return $"❌ Reboot failed for {serialNumber}.\nError: {ex.Message}";
            }
        }

        public async Task<string> PingDeviceAsync(string ipAddress)
        {
            try
            {
                var ping = new Ping();
                var reply = await ping.SendPingAsync(ipAddress, 3000);

                return reply.Status == IPStatus.Success
                    ? $"✅ Ping to {ipAddress} successful. Roundtrip time: {reply.RoundtripTime}ms"
                    : $"❌ Ping to {ipAddress} failed. Status: {reply.Status}";
            }
            catch (Exception ex)
            {
                return $"❌ Ping to {ipAddress} failed. Error: {ex.Message}";
            }
        }

        public async Task<OnlineCheckResult> CheckIfOnlineAsync(string ipAddress, int port, string username, string password)
        {
            var result = new OnlineCheckResult();
            bool pingSuccess = false;

            try
            {
                var ping = new Ping();
                var reply = await ping.SendPingAsync(ipAddress, 3000);
                pingSuccess = reply.Status == IPStatus.Success;
                result.Success = pingSuccess;
                result.Online = pingSuccess;
                result.DmpOnline = pingSuccess;
            }
            catch
            {
                result.Success = false;
            }

            if (!pingSuccess)
            {
                try
                {
                    using var sshClient = _sshFactory(ipAddress, port, username, password);
                    sshClient.Connect();

                    if (sshClient.IsConnected)
                    {
                        result.Success = true;
                        result.Online = true;
                        result.DmpOnline = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"SSH fallback failed: {ex.Message}");
                }
            }

            return result;
        }
    }
}
