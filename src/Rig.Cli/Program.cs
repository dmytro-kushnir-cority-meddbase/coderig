using System.Text;
using Rig.Cli;

// Force UTF-8 output so the tree's box-drawing (├ └ │) and per-effect emoji glyphs survive on any
// console code page and through file/pipe redirection — the default Windows OEM code page renders them
// as '?'. Wrapped defensively: a redirected/closed handle must not crash the CLI.
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // No console attached (or it rejects the change) — output still works, just may not render glyphs.
}

Environment.ExitCode = await CliApplication.RunAsync(args, output: Console.Out, error: Console.Error);
