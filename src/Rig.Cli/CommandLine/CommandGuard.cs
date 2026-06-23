using System.Data.Common;

namespace Rig.Cli.CommandLine;

// The uniform action wrapper for every command: translates a SQLite/EF DbException that escapes the body
// into the same clean, actionable store-error message (exit 2) the old DispatchAsync produced — so a query
// run in the wrong directory never dumps a raw stack trace. Also the single home for the "no indexed run"
// message shared by runs/di/files/graph.
internal static class CommandGuard
{
    internal static async Task<int> RunGuardedAsync(string workingDirectory, TextWriter error, Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (StoreRefNotFoundException notFound)
        {
            error.WriteLine($"No indexed store matches --store '{notFound.StoreRef}'.");
            error.WriteLine(
                notFound.Available.Count == 0
                    ? "Nothing is indexed here. Run `rig index <solution>` first."
                    : "Indexed stores: " + string.Join(separator: ", ", values: notFound.Available) + "."
            );
            return 2;
        }
        catch (DbException exception)
        {
            // A SQLite error escaping a command almost always means the store is missing (wrong cwd) or
            // was built by an older rig (schema drift — a column/table added since). Translate the raw
            // EF/SQLite stack trace into a clean, actionable message, mirroring how `index` already handles
            // a bad target path.
            return StoreError(workingDirectory, exception, error);
        }
    }

    // Clean exit-2 message for a store that can't be read: distinguish "no store here" (wrong directory)
    // from "older-rig schema mismatch" (re-index needed) from any other read failure.
    internal static int StoreError(string workingDirectory, DbException exception, TextWriter error)
    {
        var dbPath = StoreLayout.DbPath(workingDirectory);
        if (!File.Exists(dbPath))
        {
            error.WriteLine($"No .rig store found in '{workingDirectory}'.");
            error.WriteLine("Run `rig index <solution>` to create one, or cd to the directory that contains .rig/.");
        }
        else if (
            exception.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
        )
        {
            error.WriteLine($"The store at {dbPath} was built by an older rig (schema mismatch: {exception.Message}).");
            error.WriteLine("Re-index with the current rig: `rig index <solution>`.");
        }
        else
        {
            error.WriteLine($"Could not read the store at {dbPath}: {exception.Message}");
        }
        return 2;
    }

    internal static int NoRunError(TextWriter error)
    {
        error.WriteLine("No indexed run found. Run `rig index <solution>` first.");
        return 2;
    }
}
