using System.Net;
using System.Net.Sockets;

namespace WKRace.Server
{
    // How an IP address is written to the server log: shortened, so the log does not hold full addresses. The part
    // that is left names the network (enough to see a pattern of abuse), not the single connection.
    internal static class LogAddress
    {
        // IPv4 keeps its first three numbers ("203.0.113.x"); IPv6 its first three groups, the /48 ("2001:db8:12:x").
        public static string Short(IPAddress address)
        {
            if (address == null) return "?";
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            byte[] b = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork) return b[0] + "." + b[1] + "." + b[2] + ".x";
            if (address.AddressFamily != AddressFamily.InterNetworkV6) return "?";
            return ((b[0] << 8) | b[1]).ToString("x") + ":" + ((b[2] << 8) | b[3]).ToString("x") + ":" + ((b[4] << 8) | b[5]).ToString("x") + ":x";
        }

        public static string Short(string address)
        {
            IPAddress parsed;
            return IPAddress.TryParse(address, out parsed) ? Short(parsed) : "?";
        }
    }
}
