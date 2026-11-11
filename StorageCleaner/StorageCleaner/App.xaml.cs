using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Vacumify;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args.Any(a =>
            string.Equals(a, "--mcp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-mcp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "mcp", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AttachConsole(ATTACH_PARENT_PROCESS);
            try
            {
                Console.InputEncoding = System.Text.Encoding.UTF8;
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch { }

            McpServer.Run();
            Shutdown(0);
            return;
        }

        if (e.Args.Length > 0 && e.Args.Any(a => a is "--help" or "-h" or "/?" or "--version" or "-v"))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            Console.WriteLine("Vacumify v1.0.0 — Modern Windows Storage Cleaner & Duplicate File Finder");
            Console.WriteLine("Usage:");
            Console.WriteLine("  Vacumify.exe            Launch the graphical desktop application");
            Console.WriteLine("  Vacumify.exe --mcp      Run the Model Context Protocol (MCP) server over stdio");
            Console.WriteLine("  Vacumify.exe --help     Show this help message");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(0);
            return;
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}

