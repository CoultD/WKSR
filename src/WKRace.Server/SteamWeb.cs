using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WKRace.Server
{
    public struct WebReply
    {
        public int Status;
        public string Text;
    }

    // The server's web side: /steam/return, where Steam sends players back after signing in, /privacy, the privacy
    // notice, and POST /report, where WKSR sends bug reports. It listens on 127.0.0.1 only; nginx publishes it under
    // the server's https address with the real certificate. One request per connection, then close.
    internal sealed class SteamWeb
    {
        private const int MaxHeadBytes = 16 * 1024;
        private const int RequestTimeoutMs = 10000;
        // A report can be 20 MB; nginx buffers the upload, so this only has to cover a local copy.
        private const int BodyTimeoutMs = 120000;

        private readonly TcpListener _listener;
        private readonly string _publicUrl;
        private readonly Func<string, Task<bool>> _isPending;
        private readonly Func<string, ulong, Task<SteamLinkOutcome>> _complete;
        private readonly Func<List<KeyValuePair<string, string>>, Task<bool>> _verify;
        private readonly Action<string> _log;
        private readonly string _privacyHtml;
        private readonly Func<byte[], Task<WebReply>> _report;

        // isPending and complete run on the server's core thread; verify asks Steam (check_authentication).
        public SteamWeb(int port, string publicUrl, Func<string, Task<bool>> isPending, Func<string, ulong, Task<SteamLinkOutcome>> complete,
            Func<List<KeyValuePair<string, string>>, Task<bool>> verify, Action<string> log, string privacyHtml = null,
            Func<byte[], Task<WebReply>> report = null)
        {
            _privacyHtml = privacyHtml;
            _report = report;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _publicUrl = publicUrl;
            _isPending = isPending;
            _complete = complete;
            _verify = verify;
            _log = log ?? (s => { });
        }

        public int Port { get { return ((IPEndPoint)_listener.LocalEndpoint).Port; } }

        public void Start(CancellationToken token)
        {
            _listener.Start();
            token.Register(() => _listener.Stop());
            Task.Run(() => AcceptLoopAsync(token));
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    Request request = await ReadRequestAsync(stream).ConfigureAwait(false);
                    string target = request == null ? null : request.Target;
                    int status;
                    string body;
                    if (request != null && request.Method == "POST")
                    {
                        WebReply reply = await HandleReportAsync(stream, request).ConfigureAwait(false);
                        await WriteResponseAsync(stream, reply.Status, reply.Text, "text/plain").ConfigureAwait(false);
                        return;
                    }
                    if (target == null)
                    {
                        status = 400;
                        body = Page("Bad request", "That request could not be read.");
                    }
                    else
                    {
                        int q = target.IndexOf('?');
                        string path = q < 0 ? target : target.Substring(0, q);
                        if (path == "/privacy" && _privacyHtml != null)
                        {
                            status = 200;
                            body = _privacyHtml;
                        }
                        else if (path != "/steam/return")
                        {
                            status = 404;
                            body = Page("Not found", "There is nothing here.");
                        }
                        else
                        {
                            status = 200;
                            body = await HandleReturnAsync(q < 0 ? "" : target.Substring(q + 1)).ConfigureAwait(false);
                        }
                    }
                    await WriteResponseAsync(stream, status, body, "text/html").ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log("steam sign-in page error: " + e.Message);
                }
            }
        }

        private async Task<string> HandleReturnAsync(string query)
        {
            Dictionary<string, string> q = SteamOpenId.ParseQuery(query);
            string nonce;
            q.TryGetValue("n", out nonce);
            if (!await _isPending(nonce).ConfigureAwait(false))
                return Page("Sign-in expired", "This sign-in link has expired or was already used. Start it again from WKSR.");

            string error;
            ulong steamId = SteamOpenId.Validate(q, SteamOpenId.ReturnUrl(_publicUrl, nonce), out error);
            if (steamId == 0) return Page("Sign-in failed", error);

            bool confirmed;
            try { confirmed = await _verify(SteamOpenId.CheckAuthenticationForm(q)).ConfigureAwait(false); }
            catch (Exception e)
            {
                _log("could not reach Steam to confirm a sign-in: " + e.Message);
                return Page("Sign-in failed", "Steam could not be reached to confirm the sign-in. Try again in a moment.");
            }
            if (!confirmed) return Page("Sign-in failed", "Steam did not confirm this sign-in. Start it again from WKSR.");

            SteamLinkOutcome outcome = await _complete(nonce, steamId).ConfigureAwait(false);
            return Page(outcome.Ok ? "Signed in with Steam" : "Not linked", outcome.Message);
        }

        private sealed class Request
        {
            public string Method;
            public string Target;
            public long ContentLength = -1;
            // Body bytes that arrived with the head.
            public byte[] Start = new byte[0];
        }

        // Reads the request head. Returns null unless it is a GET, or a POST to /report.
        private static async Task<Request> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[MaxHeadBytes];
            int length = 0, headEnd = -1;
            using (var cts = new CancellationTokenSource(RequestTimeoutMs))
            {
                while (length < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer, length, buffer.Length - length, cts.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    length += n;
                    headEnd = IndexOfHeadEnd(buffer, length);
                    if (headEnd >= 0) break;
                }
            }
            if (headEnd < 0) return null;
            string[] lines = Encoding.ASCII.GetString(buffer, 0, headEnd + 1).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] parts = lines[0].Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !parts[1].StartsWith("/", StringComparison.Ordinal)) return null;
            var request = new Request { Method = parts[0], Target = parts[1] };
            if (request.Method == "POST")
            {
                if (request.Target != "/report") return null;
                foreach (string line in lines)
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon).Trim();
                    if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) return null;
                    long value;
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && long.TryParse(line.Substring(colon + 1).Trim(), out value))
                        request.ContentLength = value;
                }
            }
            else if (request.Method != "GET") return null;
            request.Start = new byte[length - headEnd - 1];
            Buffer.BlockCopy(buffer, headEnd + 1, request.Start, 0, request.Start.Length);
            return request;
        }

        private async Task<WebReply> HandleReportAsync(NetworkStream stream, Request request)
        {
            if (_report == null) return new WebReply { Status = 404, Text = "This server does not take bug reports." };
            if (request.ContentLength < 0) return new WebReply { Status = 411, Text = "Content-Length required." };
            if (request.ContentLength > WKRace.Protocol.BugReport.MaxBodyBytes) return new WebReply { Status = 413, Text = "The report is too large." };
            var body = new byte[request.ContentLength];
            int length = Math.Min(request.Start.Length, body.Length);
            Buffer.BlockCopy(request.Start, 0, body, 0, length);
            using (var cts = new CancellationTokenSource(BodyTimeoutMs))
            {
                while (length < body.Length)
                {
                    int n = await stream.ReadAsync(body, length, body.Length - length, cts.Token).ConfigureAwait(false);
                    if (n == 0) return new WebReply { Status = 400, Text = "The upload was cut off." };
                    length += n;
                }
            }
            return await _report(body).ConfigureAwait(false);
        }

        private static int IndexOfHeadEnd(byte[] buffer, int length)
        {
            for (int i = 3; i < length; i++)
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n') return i;
            return -1;
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int status, string body, string type)
        {
            byte[] content = Encoding.UTF8.GetBytes(body);
            string reason = status == 200 ? "OK" : status == 403 ? "Forbidden" : status == 404 ? "Not Found" : status == 411 ? "Length Required"
                : status == 413 ? "Payload Too Large" : status == 429 ? "Too Many Requests" : status == 503 ? "Service Unavailable" : "Bad Request";
            string head =
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: " + type + "; charset=utf-8\r\n" +
                "Content-Length: " + content.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Referrer-Policy: no-referrer\r\n" +
                "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(content, 0, content.Length).ConfigureAwait(false);
        }

        // A plain page in the WKSR colours. Everything shown is escaped.
        private static string Page(string title, string message)
        {
            string t = WebUtility.HtmlEncode(title);
            string m = WebUtility.HtmlEncode(message ?? "");
            return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                   "<title>WKSR - " + t + "</title><style>" +
                   "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:#160d0d;color:#d39a5f;font:18px/1.5 monospace}" +
                   "main{max-width:560px;margin:16px;padding:28px 32px;border:3px solid #be824d}" +
                   "h1{margin:0 0 12px;color:#eaa142;font-size:28px;letter-spacing:2px}p{margin:0}" +
                   "</style></head><body><main><h1>WKSR</h1><h1>" + t + "</h1><p>" + m + "</p></main></body></html>";
        }

        // Asks Steam to confirm the assertion it sent with the browser.
        public static async Task<bool> CheckWithSteam(HttpClient http, List<KeyValuePair<string, string>> form)
        {
            using (var content = new FormUrlEncodedContent(form))
            using (HttpResponseMessage response = await http.PostAsync(SteamOpenId.Endpoint, content).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return false;
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return SteamOpenId.IsValidResponse(body);
            }
        }
    }
}
