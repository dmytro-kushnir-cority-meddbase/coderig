using System.ComponentModel;
using System.Diagnostics;

namespace Rig.Analysis;

// Memory-profiling instrumentation, gated by env vars (no-op when neither is set, so it costs nothing in
// a normal run). Two modes at each instrumented point:
//
//   RIG_PROFILE_DUMP=gcdump|dump|both  — AUTO-CAPTURE: the app shells out to dotnet-gcdump / dotnet-dump
//       targeting its OWN pid and writes a snapshot at the labeled point, then continues. This is the
//       hands-off path: one run drops a dump at each peak with no second terminal. A full `dump` briefly
//       suspends the process while createdump writes it (~the heap size on disk), which is fine — the
//       collector resumes us when done. Output dir = RIG_PROFILE_DIR or the cwd.
//
//   RIG_PROFILE_PAUSE=1                 — INTERACTIVE: print the pid + a ready-to-run command and wait for
//       ENTER so a snapshot can be taken by hand. Safe on a non-TTY (ReadLine returns null -> continue).
//
// Set both to auto-capture AND pause afterwards. The tools are resolved from %USERPROFILE%\.dotnet\tools
// (the global-tool install dir), falling back to PATH. Everything here is best-effort: a missing tool or a
// failed capture logs and continues — profiling instrumentation must never fail the index.
public static class ProfilingPause
{
    public static void MaybePause(string label)
    {
        var dumpMode = Environment.GetEnvironmentVariable("RIG_PROFILE_DUMP");
        var pause = Environment.GetEnvironmentVariable("RIG_PROFILE_PAUSE");
        if (string.IsNullOrEmpty(dumpMode) && string.IsNullOrEmpty(pause))
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        var pid = process.Id;
        var workingSetMb = process.WorkingSet64 / (1024 * 1024);
        var managedMb = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);

        Console.WriteLine();
        Console.WriteLine($"[profile-pause] === {label} ===");
        Console.WriteLine($"[profile-pause] PID {pid} | working set {workingSetMb} MB | managed heap {managedMb} MB");

        if (!string.IsNullOrEmpty(dumpMode))
        {
            CaptureSelf(label: label, mode: dumpMode, pid: pid);
        }

        if (!string.IsNullOrEmpty(pause))
        {
            Console.WriteLine($"[profile-pause] gcdump:  dotnet-gcdump collect -p {pid} -o \"{label}.gcdump\"");
            Console.WriteLine($"[profile-pause] dump:    dotnet-dump collect -p {pid} -o \"{label}.dmp\"");
            Console.WriteLine("[profile-pause] press ENTER to continue...");
            Console.ReadLine();
        }
    }

    // AUTO-CAPTURE: spawn the diagnostics tool(s) against our own pid and wait for each to finish.
    private static void CaptureSelf(string label, string mode, int pid)
    {
        var dir = Environment.GetEnvironmentVariable("RIG_PROFILE_DIR");
        dir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir;
        var stem = Path.Combine(dir, SanitizeLabel(label));

        if (mode.Contains("gcdump", StringComparison.OrdinalIgnoreCase) || mode.Contains("both", StringComparison.OrdinalIgnoreCase))
        {
            Capture(tool: "dotnet-gcdump", arguments: $"collect -p {pid} -o \"{stem}.gcdump\"");
        }

        if (mode.Contains("dump", StringComparison.OrdinalIgnoreCase) || mode.Contains("both", StringComparison.OrdinalIgnoreCase))
        {
            // A full dump suspends this process while createdump writes it; the collector resumes us on exit.
            Capture(tool: "dotnet-dump", arguments: $"collect -p {pid} -o \"{stem}.dmp\"");
        }
    }

    // Run one diagnostics tool synchronously, inheriting our console so its progress streams inline.
    private static void Capture(string tool, string arguments)
    {
        var exe = ResolveTool(tool);
        Console.WriteLine($"[profile-pause] capturing: {tool} {arguments}");
        var watch = Stopwatch.StartNew();
        try
        {
            using var proc = Process.Start(
                new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    UseShellExecute = false,
                }
            );
            if (proc is null)
            {
                Console.WriteLine($"[profile-pause] could not start {tool}");
                return;
            }

            proc.WaitForExit();
            Console.WriteLine($"[profile-pause] {tool} exited {proc.ExitCode} in {watch.Elapsed.TotalSeconds:0.0}s");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            // Tool not installed / not on PATH / spawn failure — log and carry on; profiling never aborts the run.
            Console.WriteLine($"[profile-pause] {tool} capture failed: {exception.Message}");
            Console.WriteLine($"[profile-pause] install it with:  dotnet tool install -g {tool}");
        }
    }

    // Global tools live in %USERPROFILE%\.dotnet\tools; fall back to the bare name (PATH) if not there.
    private static string ResolveTool(string tool)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(home, ".dotnet", "tools", tool + ".exe");
        return File.Exists(candidate) ? candidate : tool;
    }

    // Filesystem-safe stem from a human label ("extract-peak (roslyn live)" -> "extract-peak-roslyn-live").
    private static string SanitizeLabel(string label)
    {
        var chars = label.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-').Replace("--", "-", StringComparison.Ordinal);
    }
}
