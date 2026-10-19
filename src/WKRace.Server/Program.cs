using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WKRace.Server
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // "WKRace.Server admin <command>" talks to a running server instead of starting one.
            if (args.Length > 0 && args[0] == "admin") return AdminPort.RunClient(args);

            ServerOptions options;
            try
            {
                options = ServerOptions.Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }

            RaceServer server;
            try
            {
                server = new RaceServer(options, line => Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " " + line));
            }
            catch (InvalidOperationException e)
            {
                // For example the database key is wrong or missing.
                Console.Error.WriteLine(e.Message);
                return 3;
            }
            server.Start();

            var exit = new ManualResetEventSlim();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                exit.Set();
            };
            // systemctl stop sends SIGTERM: let Main finish (players saved, database closed) and exit with 0.
            using (PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                exit.Set();
            }))
            {
                exit.Wait();
            }

            server.StopAsync().GetAwaiter().GetResult();
            return 0;
        }
    }
}
