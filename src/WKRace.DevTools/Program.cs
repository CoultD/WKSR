using System;

namespace WKRace.DevTools
{
    // Development helpers. "bot" signs in to a race server as a second player, so matches can be tried alone.
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "bot")
            {
                return Bot.Run(args);
            }
            Console.Error.WriteLine("Usage: WKRace.DevTools bot [--server host:port] [options]  (\"bot --help\" lists the options)");
            return 2;
        }
    }
}
