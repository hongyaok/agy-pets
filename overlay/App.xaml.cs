using System.Windows;

namespace AgyPetsOverlay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!TryResolvePort(e.Args, out int port))
        {
            Console.Error.WriteLine("Usage: AgyPetsOverlay.exe --port <port> (or set ANTIGRAVITY_SIDECAR_WEB_PORT)");
            Shutdown(1);
            return;
        }

        var window = new MainWindow(port);
        MainWindow = window;
        window.Show();
    }

    private static bool TryResolvePort(string[] args, out int port)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out port) && IsValidPort(port))
            {
                return true;
            }

            if (args[i].StartsWith("--port=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i]["--port=".Length..], out port) && IsValidPort(port))
            {
                return true;
            }
        }

        string? envPort = Environment.GetEnvironmentVariable("ANTIGRAVITY_SIDECAR_WEB_PORT")
                          ?? Environment.GetEnvironmentVariable("AGY_PETS_PORT");
        if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out port) && IsValidPort(port))
        {
            return true;
        }

        port = 0;
        return false;
    }

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;
}
