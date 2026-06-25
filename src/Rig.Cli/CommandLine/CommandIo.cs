namespace Rig.Cli.CommandLine;

internal sealed record WorkspaceLocation(string WorkingDirectory, string? StoreRef = null);

internal sealed record TextOutput(TextWriter Output, TextWriter Error);

// Ambient host wiring threaded into every command's RunAsync, alongside its command-specific options record.
// These four are not user-facing options in the same sense as the rest — Output/Error are the writers the
// command Build() captured, WorkingDirectory locates the store/rules, and StoreRef is the resolved `--store`
// value (null for commands without a `--store` option, e.g. `dead`/`impact`). Bundling them keeps each
// RunAsync to two parameters (the options record + this) instead of a long positional tail.

internal sealed record CommandIo(TextOutput TextOutput, WorkspaceLocation WorkspaceLocation);
