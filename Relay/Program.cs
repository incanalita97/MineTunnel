using System;
using System.Threading;

namespace MineTunnel.Relay
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            Console.Title = "MineTunnel Relay Server - Zero Port Forwarding Hub";
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(@"
  __  __ _            _____                     _   _____      _             
 |  \/  (_)          |_   _|                   | | |  __ \    | |            
 | \  / |_ _ __   ___  | |_   _ _ __  _ __   ___| | | |__) |___| | __ _ _   _ 
 | |\/| | | '_ \ / _ \ | | | | | '_ \| '_ \ / _ \ | |  _  // _ \ |/ _` | | | |
 | |  | | | | | |  __/ | | |_| | | | | | | |  __/ | | | \ \  __/ | (_| | |_| |
 |_|  |_|_|_| |_|\___| |_|\__,_|_| |_|_| |_|\___|_| |_|  \_\___|_|\__,_|\__, |
                                                                         __/ |
                                                                        |___/ ");
            Console.ResetColor();
            Console.WriteLine(" Universal Minecraft Java Tunnel Relay (Bypasses CGNAT & Locked Routers)\n");

            int port = 25560;
            if (args.Length > 0)
            {
                int p;
                if (int.TryParse(args[0], out p)) port = p;
            }

            RelayServer server = new RelayServer(port);
            server.Start();

            Console.WriteLine("Instructions:");
            Console.WriteLine(" 1. In MineTunnel on the host PC: Select 'Relay Mode' and enter this machine's IP:{0}", port);
            Console.WriteLine(" 2. Friends connect to this machine's IP:{0} with the Host's Room Name and Password.", port);
            Console.WriteLine(" 3. Press Ctrl+C to stop the relay server.\n");

            ManualResetEvent exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                server.Stop();
                exitEvent.Set();
            };

            exitEvent.WaitOne();
            Console.WriteLine("Relay server stopped.");
        }
    }
}
