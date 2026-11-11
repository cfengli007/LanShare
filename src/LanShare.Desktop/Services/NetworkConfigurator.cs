using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanShare.Desktop.Services;

public sealed record AdapterOption(
    string Name,
    string Description,
    string Address,
    string Gateway,
    int PrefixLength,
    bool DhcpEnabled)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Address)
        ? $"{Description} ({Name})"
        : $"{Description} ({Name}) - {Address}";

    public override string ToString() => DisplayName;
}

/// <summary>读取网卡信息，并通过管理员 netsh 修改 IPv4 配置。</summary>
public static class NetworkConfigurator
{
    public static IReadOnlyList<AdapterOption> GetAdapters()
    {
        var result = new List<AdapterOption>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                var gateway = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? string.Empty;
                var prefix = ipv4?.PrefixLength ?? 24;

                result.Add(new AdapterOption(
                    nic.Name,
                    nic.Description,
                    ipv4?.Address.ToString() ?? string.Empty,
                    gateway,
                    prefix,
                    props.DhcpServerAddresses.Count > 0));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("NetworkConfigurator.GetAdapters", ex);
        }
        return result;
    }

    public static Task<ElevationResult> ApplyStaticAsync(string adapterName, string address, int prefixLength, string gateway)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
            return Task.FromResult(new ElevationResult(false, "请先选择网卡。"));
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return Task.FromResult(new ElevationResult(false, "IP 地址格式不正确。"));
        if (prefixLength is < 1 or > 32)
            return Task.FromResult(new ElevationResult(false, "子网前缀需在 1-32 之间。"));
        if (!string.IsNullOrWhiteSpace(gateway) &&
            (!IPAddress.TryParse(gateway, out var gw) || gw.AddressFamily != AddressFamily.InterNetwork))
            return Task.FromResult(new ElevationResult(false, "网关地址格式不正确。"));
        if (adapterName.Contains('"'))
            return Task.FromResult(new ElevationResult(false, "网卡名称包含不支持的字符。"));

        var mask = PrefixToMask(prefixLength);
        var gatewayArg = string.IsNullOrWhiteSpace(gateway) ? string.Empty : $" gateway=\"{gateway}\"";
        var script = $@"
netsh interface ipv4 set address name=""{adapterName}"" source=static address={address} mask={mask}{gatewayArg}
";
        return ElevationHelper.RunElevatedAsync("修改 IP 地址", script);
    }

    public static Task<ElevationResult> ApplyDhcpAsync(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
            return Task.FromResult(new ElevationResult(false, "请先选择网卡。"));
        if (adapterName.Contains('"'))
            return Task.FromResult(new ElevationResult(false, "网卡名称包含不支持的字符。"));

        var script = $@"
netsh interface ipv4 set address name=""{adapterName}"" source=dhcp
netsh interface ipv4 set dnsservers name=""{adapterName}"" source=dhcp
";
        return ElevationHelper.RunElevatedAsync("切换为自动获取 IP", script);
    }

    public static string PrefixToMask(int prefixLength)
    {
        prefixLength = Math.Clamp(prefixLength, 0, 32);
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var bytes = BitConverter.GetBytes(mask);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return new IPAddress(bytes).ToString();
    }
}
