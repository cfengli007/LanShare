using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanShare.Core;

public static class NetworkInfo
{
    public static IReadOnlyList<NetworkAddress> GetIPv4Addresses()
    {
        var result = new List<NetworkAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    result.Add(new NetworkAddress(nic.Name, addr.Address.ToString(), nic.Description));
                }
            }
        }
        catch
        {
            // ignore and fall back below
        }
        return result;
    }

    public static string GetPrimaryIPv4()
    {
        var addresses = GetIPv4Addresses();
        // Prefer 192.168 / 10 / 172.16-31 private addresses, then anything else.
        static bool IsPrivate(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
        }
        foreach (var a in addresses)
        {
            if (IPAddress.TryParse(a.Address, out var ip) && IsPrivate(ip)) return a.Address;
        }
        return addresses.FirstOrDefault()?.Address ?? "127.0.0.1";
    }
}
