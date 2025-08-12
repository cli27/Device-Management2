namespace SshDeviceToolkit
{

    public sealed class DeviceDetails
    {
        public string? Ip1 { get; init; }      // Primary (WAN/Mobile)
        public string? Ip2 { get; init; }      // Secondary (LAN)
        public string WanMacRaw { get; init; } = "";
        public long Ip1RuntimeMs { get; init; } // Time to find Ip1
        public long Ip2RuntimeMs { get; init; } // Time to find Ip2
    }
}
