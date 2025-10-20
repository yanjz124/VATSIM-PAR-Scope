using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RadarStreamerReceiver
{
    class Program
    {
        static void Main(string[] args)
        {
            int port = 49090;
            int p;
            if (args.Length == 1 && int.TryParse(args[0], out p))
                port = p;

            Console.WriteLine("VATSIM PAR Scope - Radar Streamer Receiver");
            Console.WriteLine("Listening on UDP 0.0.0.0:" + port + " ... (Ctrl+C to quit)");
            Console.WriteLine();

            using (var client = new UdpClient(port))
            {
                var ep = new IPEndPoint(IPAddress.Any, port);
                while (true)
                {
                    try
                    {
                        var data = client.Receive(ref ep);
                        var text = Encoding.UTF8.GetString(data);
                        Console.Write(text);
                    }
                    catch (SocketException se)
                    {
                        Console.WriteLine("Socket error: " + se.Message);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error: " + ex.Message);
                    }
                }
            }
        }
    }
}
