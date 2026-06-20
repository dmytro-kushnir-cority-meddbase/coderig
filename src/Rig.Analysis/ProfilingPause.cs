using System.Diagnostics;

namespace Rig.Analysis;

// Env-gated pause for memory profiling. Set RIG_PROFILE_PAUSE=1 and the index stops at each
// instrumented point, prints the PID + a ready-to-run snapshot command, and waits for ENTER so a
// heap snapshot can be captured at that exact moment with the standard .NET diagnostics tools
// (dotnet-gcdump / dotnet-dump). A no-op when the variable is unset, so it costs nothing normally.
// Not interactive (stdin redirected/EOF) -> ReadLine returns null and the run continues, so it never
// hangs a non-TTY run.
public static class ProfilingPause
{
    public static void MaybePause(string label)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RIG_PROFILE_PAUSE")))
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        var workingSetMb = process.WorkingSet64 / (1024 * 1024);
        var managedMb = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);

        Console.WriteLine();
        Console.WriteLine($"[profile-pause] === {label} ===");
        Console.WriteLine($"[profile-pause] PID {process.Id} | working set {workingSetMb} MB | managed heap {managedMb} MB");
        Console.WriteLine($"[profile-pause] gcdump:  dotnet-gcdump collect -p {process.Id} -o \"{label}.gcdump\"");
        Console.WriteLine($"[profile-pause] dump:    dotnet-dump collect -p {process.Id} -o \"{label}.dmp\"");
        Console.WriteLine("[profile-pause] capture now, then press ENTER to continue...");
        Console.ReadLine();
    }
}
