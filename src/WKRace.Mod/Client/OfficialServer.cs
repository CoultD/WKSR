using WKRace.Protocol;

namespace WKRace.Client
{
    // Where the client connects. The official server is built in and never written to a settings file.
    internal sealed class ServerEndpoint
    {
        public string Host;
        public int Port;
        public bool Tls;
        // True for a --server override used for local testing.
        public bool Custom;
        // Where WKSR posts bug reports (null: no report button).
        public string ReportUrl;
    }

    internal static class OfficialServer
    {
        // A host name, not an address: the server can move without a new WKSR. It is public anyway (Steam sign-in and
        // the privacy notice are on it), so it is kept in plain sight; Thunderstore does not accept hidden code or data.
        public const string Host = "host.unlimitedgamesnogames.com";

        public static ServerEndpoint Endpoint()
        {
            return new ServerEndpoint { Host = Host, Port = ProtocolConstants.DefaultServerPort, Tls = true, ReportUrl = "https://" + Host + "/report" };
        }
    }
}
