using System.CommandLine;
using Rig.Cli.Commands;

namespace Rig.Cli.CommandLine;

// Assembles the rig root command from the per-command Build factories. Each command closes over the shared
// output/error writers + working directory, so the framework owns parsing/help/version/error-chrome and just
// dispatches to the command actions. This is the single place the CLI surface (the 15 subcommands) is
// declared; ordering here is the order they list in `rig --help`.
internal static class Root
{
    internal static RootCommand Build(TextWriter output, TextWriter error, string workingDirectory) =>
        new("Runtime Intelligence Graph")
        {
            IndexCommands.BuildIndex(output, error, workingDirectory),
            IndexCommands.BuildMine(output, error, workingDirectory),
            FactCommands.BuildRuns(output, error, workingDirectory),
            FactCommands.BuildDi(output, error, workingDirectory),
            FactCommands.BuildSymbols(output, error, workingDirectory),
            FactCommands.BuildRefs(output, error, workingDirectory),
            PathCommand.Build(output, error, workingDirectory),
            TreeCommand.Build(output, error, workingDirectory),
            CallersCommand.Build(output, error, workingDirectory),
            ReachesCommand.Build(output, error, workingDirectory),
            DeriveCommand.Build(output, error, workingDirectory),
            ImpactCommand.Build(output, error, workingDirectory),
            IndexCommands.BuildGraph(output, error, workingDirectory),
            // `dead` is DISABLED for now: it runs on the receiver-blind SQL reachability superset, which
            // since the one-hop dispatch fix (FactPathFinder.Successors `fromDispatch`) no longer matches
            // the precise tree/reaches/path engine. Rather than ship a dead-code report that disagrees with
            // what `tree` shows reachable, it stays unregistered until it's moved onto the same engine.
            // The command + DeadCommand.cs are retained; re-add this line to re-enable. (No users yet.)
            // DeadCommand.Build(output, error, workingDirectory),
            FactCommands.BuildFiles(output, error, workingDirectory),
            FactCommands.BuildProfile(output, error, workingDirectory),
        };
}
