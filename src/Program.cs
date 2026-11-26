using System;
using System.Threading.Tasks;

namespace MusicVisualizerLoopback
{
    internal static class Program
    {
        [STAThread]
        private static async Task Main(string[] args)
        {
            using var app = PluginApp.FromArgs(args);
            await app.RunAsync();
        }
    }
}
