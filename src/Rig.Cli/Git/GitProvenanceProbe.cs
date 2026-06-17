using System.Diagnostics;
using Rig.Domain.Data;

namespace Rig.Cli.Git;

// Captures the source-control provenance of an index (commit / branch / dirty-state) by shelling out to
// git, returning the Rig.Domain GitProvenance record. The shell-out lives in the CLI (not the storage or
// domain layer) — same pattern as ImpactCommand's git diff. Best-effort by contract: any failure (not a
// work tree, git not on PATH, detached/empty repo) returns GitProvenance.None so `rig index` never fails
// because of git. See docs/design-impact-behavioral-diff.md §4.5.
internal static class GitProvenanceProbe
{
    // Provenance for the work tree containing `pathInsideRepo` (a solution/project path or a directory).
    public static GitProvenance Capture(string pathInsideRepo)
    {
        var dir = File.Exists(pathInsideRepo) ? Path.GetDirectoryName(pathInsideRepo) ?? pathInsideRepo : pathInsideRepo;

        var commit = Run(dir, "rev-parse", "HEAD");
        if (string.IsNullOrEmpty(commit))
        {
            return GitProvenance.None; // not a git work tree, or git unavailable — provenance simply absent
        }

        var branch = Run(dir, "rev-parse", "--abbrev-ref", "HEAD");
        var status = Run(dir, "status", "--porcelain");
        return new GitProvenance(
            Commit: commit,
            Branch: string.IsNullOrEmpty(branch) ? null : branch,
            Dirty: !string.IsNullOrEmpty(status)
        );
    }

    private static string? Run(string workingDir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
