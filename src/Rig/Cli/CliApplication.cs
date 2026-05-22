namespace Rig.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteCommandSummary(output);
            return Task.FromResult(0);
        }

        if (IsVersion(args[0]))
        {
            output.WriteLine("rig 0.0.0");
            return Task.FromResult(0);
        }

        error.WriteLine($"Unknown command: {args[0]}");
        error.WriteLine("Run `rig --help` to see available commands.");
        return Task.FromResult(2);
    }

    private static bool IsHelp(string arg)
    {
        return arg is "--help" or "-h" or "help";
    }

    private static bool IsVersion(string arg)
    {
        return arg is "--version" or "-v" or "version";
    }

    private static void WriteCommandSummary(TextWriter output)
    {
        output.WriteLine("Runtime Intelligence Graph");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  rig index <solution>");
        output.WriteLine("  rig runs");
        output.WriteLine("  rig entrypoints");
        output.WriteLine("  rig callgraph <entrypoint-id>");
        output.WriteLine("  rig effects --entrypoint <entrypoint-id>");
        output.WriteLine("  rig files --skipped");
        output.WriteLine("  rig profile validate");
    }
}
