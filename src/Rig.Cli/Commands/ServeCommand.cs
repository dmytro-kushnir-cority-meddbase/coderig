using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Rig.Cli.Web;

namespace Rig.Cli.Commands;

// `rig serve` — boot the in-process web GUI over the .rig store in the current directory (same cwd-locates-
// store model as every query command). Hosts a minimal API (/api/*) + the static React SPA, opens the
// browser, and runs until Ctrl+C. This is the ONLY command that starts a long-running host; every other
// command is a one-shot query.
internal static class ServeCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var port = new Option<int>("--port") { Description = "Local port to listen on.", DefaultValueFactory = _ => 5050 };
        var noOpen = new Option<bool>("--no-open") { Description = "Do not open the browser automatically." };
        var cmd = new Command(name: "serve", description: "Serve the interactive web tree/effects explorer over the current .rig store.")
        {
            port,
            noOpen,
        };
        cmd.SetAction(pr => RunAsync(pr.GetValue(port), pr.GetValue(noOpen), output, error, workingDirectory));
        return cmd;
    }

    private static async Task<int> RunAsync(int port, bool noOpen, TextWriter output, TextWriter error, string workingDirectory)
    {
        var app = RigWebHost.Build(workingDirectory, port);
        var url = $"http://localhost:{port}";
        try
        {
            await app.StartAsync();
        }
        catch (Exception ex)
        {
            error.WriteLine($"Failed to start server on {url}: {ex.Message}");
            return 1;
        }

        output.WriteLine($"rig serve — listening on {url}");
        output.WriteLine($"  store dir: {workingDirectory}");
        output.WriteLine("  press Ctrl+C to stop.");
        if (!noOpen)
        {
            TryOpenBrowser(url, error);
        }

        await app.WaitForShutdownAsync();
        return 0;
    }

    // Best-effort browser launch; a failure is non-fatal (the URL is already printed for manual open).
    private static void TryOpenBrowser(string url, TextWriter error)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(fileName: "open", arguments: url);
            }
            else
            {
                Process.Start(fileName: "xdg-open", arguments: url);
            }
        }
        catch (Exception ex)
        {
            error.WriteLine($"(could not open browser automatically: {ex.Message})");
        }
    }
}
