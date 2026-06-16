using System.CommandLine;
using Rig.Cli.CommandLine;

namespace Rig.Cli;

// The CLI entry point. Parsing, help, --version, and error chrome are owned by System.CommandLine; this just
// assembles the root command (Root.Build) over the caller's writers and invokes it. Every command's logic
// lives under Commands/*, and the shared parse/format/derive/cache invariants under CommandLine/, Rendering/,
// Graph/, Effects/, EntryPoints/, Caching/, Rules/ — one concern per file.
public static class CliApplication
{
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) =>
        RunAsync(args, output, error, Directory.GetCurrentDirectory());

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        var root = Root.Build(output, error, workingDirectory);
        var configuration = new InvocationConfiguration { Output = output, Error = error };
        return await root.Parse(args).InvokeAsync(configuration);
    }
}
