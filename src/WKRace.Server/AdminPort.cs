using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WKRace.Server
{
    // Admin commands over 127.0.0.1 only. A request is the admin key on the first line and one argument per line after
    // it; the sender then closes its side and reads the answer. On the VPS the key is readable by root only, so
    // "sudo wkrace-admin ..." is the way in.
    internal sealed class AdminPort
    {
        private const int MaxRequestBytes = 64 * 1024;

        private readonly TcpListener _listener;
        private readonly byte[] _key;
        private readonly Func<string[], Task<string>> _run;
        private readonly Action<string> _log;

        public AdminPort(int port, string key, Func<string[], Task<string>> run, Action<string> log)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _key = Encoding.UTF8.GetBytes(key);
            _run = run;
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
                    string request = await ReadAllAsync(stream).ConfigureAwait(false);
                    string[] lines = request.Replace("\r", "").Split('\n');
                    string answer;
                    if (lines.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(lines[0]), _key))
                    {
                        answer = "Error: wrong admin key.\n";
                        _log("admin request with a wrong key");
                    }
                    else
                    {
                        string[] args = new string[Math.Max(0, lines.Length - 1)];
                        Array.Copy(lines, 1, args, 0, args.Length);
                        args = Array.FindAll(args, a => a.Length > 0);
                        answer = await _run(args).ConfigureAwait(false);
                    }
                    byte[] bytes = Encoding.UTF8.GetBytes(answer);
                    await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log("admin request failed: " + e.Message);
                }
            }
        }

        private static async Task<string> ReadAllAsync(NetworkStream stream)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            using (var cts = new CancellationTokenSource(10000))
            {
                int n;
                while ((n = await stream.ReadAsync(chunk, 0, chunk.Length, cts.Token).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, n);
                    if (buffer.Length > MaxRequestBytes) throw new IOException("admin request too large");
                }
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        // The "WKRace.Server admin ..." side: sends the request and prints the answer. Returns the exit code.
        public static int RunClient(string[] args)
        {
            int port = 47690;
            string keyFile = null;
            var command = new System.Collections.Generic.List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--admin-port" && i + 1 < args.Length) { port = int.Parse(args[++i]); continue; }
                if (args[i] == "--admin-key-file" && i + 1 < args.Length) { keyFile = args[++i]; continue; }
                command.Add(args[i]);
            }
            if (keyFile == null)
            {
                Console.Error.WriteLine("Usage: WKRace.Server admin --admin-key-file <file> [--admin-port <n>] <command> [arguments]\n" + ServerCore.AdminUsage);
                return 2;
            }
            string key;
            try { key = File.ReadAllText(keyFile).Trim(); }
            catch (Exception e)
            {
                Console.Error.WriteLine("Cannot read the admin key " + keyFile + ": " + e.Message + " (run with sudo?)");
                return 2;
            }
            try
            {
                using (var tcp = new TcpClient())
                {
                    tcp.Connect(IPAddress.Loopback, port);
                    NetworkStream stream = tcp.GetStream();
                    byte[] request = Encoding.UTF8.GetBytes(key + "\n" + string.Join("\n", command) + "\n");
                    stream.Write(request, 0, request.Length);
                    tcp.Client.Shutdown(SocketShutdown.Send);
                    string answer = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                    Console.Write(answer);
                    return answer.StartsWith("Error:", StringComparison.Ordinal) ? 1 : 0;
                }
            }
            catch (SocketException e)
            {
                Console.Error.WriteLine("Cannot reach the match server's admin port " + port + ": " + e.Message + " (is it running with an admin key?)");
                return 1;
            }
        }
    }
}
