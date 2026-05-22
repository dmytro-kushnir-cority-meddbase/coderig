using Rig.Cli;

Environment.ExitCode = await CliApplication.RunAsync(args, Console.Out, Console.Error);
