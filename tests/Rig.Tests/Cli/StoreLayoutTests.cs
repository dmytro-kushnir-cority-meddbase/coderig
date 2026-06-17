using Rig.Cli.CommandLine;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

// The per-commit store resolver (docs/design-impact-behavioral-diff.md §4.4): a store dir is addressed by
// commit sha (full or short) or its exact id. These are the bug-prone matching rules behind `impact --base`.
public sealed class StoreLayoutTests
{
    [Test]
    public async Task ResolveStoreDirByRef_matches_full_sha_short_prefix_exact_id_and_dirty_stem()
    {
        await using var work = TempDir.Create();
        var full = "abc123def4567890aaaa1111bbbb2222cccc3333";
        var shortId = full[..12]; // "abc123def456" — what NewStoreId would name the dir
        MakeStore(work.Path, shortId);
        MakeStore(work.Path, "def456aaa789-dirty");
        MakeStore(work.Path, "ts-20260617120000");

        // Full HEAD sha resolves the short-sha store dir (store id is a prefix of the sha).
        Path.GetFileName(StoreLayout.ResolveStoreDirByRef(work.Path, full)).ShouldBe(shortId);
        // A short prefix of the id resolves it.
        Path.GetFileName(StoreLayout.ResolveStoreDirByRef(work.Path, "abc123")).ShouldBe(shortId);
        // Exact id (incl. the -dirty store) resolves.
        Path.GetFileName(StoreLayout.ResolveStoreDirByRef(work.Path, "def456aaa789-dirty")).ShouldBe("def456aaa789-dirty");
        // The dirty store's sha stem resolves it.
        Path.GetFileName(StoreLayout.ResolveStoreDirByRef(work.Path, "def456aaa789")).ShouldBe("def456aaa789-dirty");
        // A timestamp store resolves by exact id.
        Path.GetFileName(StoreLayout.ResolveStoreDirByRef(work.Path, "ts-20260617120000")).ShouldBe("ts-20260617120000");
        // An unrelated sha resolves to nothing.
        StoreLayout.ResolveStoreDirByRef(work.Path, "9999999999999999").ShouldBeNull();
    }

    [Test]
    public async Task AvailableStoreIds_lists_only_dirs_holding_a_db_sorted()
    {
        await using var work = TempDir.Create();
        MakeStore(work.Path, "bbb222222222");
        MakeStore(work.Path, "aaa111111111");
        // A subdir without a rig.db is NOT a store.
        Directory.CreateDirectory(Path.Combine(work.Path, ".rig", "not-a-store"));

        StoreLayout.AvailableStoreIds(work.Path).ShouldBe(["aaa111111111", "bbb222222222"]);
    }

    [Test]
    public async Task AvailableStoreIds_is_empty_when_no_rig_dir()
    {
        await using var work = TempDir.Create();
        StoreLayout.AvailableStoreIds(work.Path).ShouldBeEmpty();
    }

    private static void MakeStore(string workingDirectory, string storeId)
    {
        var dir = Path.Combine(workingDirectory, ".rig", storeId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "rig.db"), ""); // presence is all the resolver checks
    }

    private sealed class TempDir : IAsyncDisposable
    {
        public required string Path { get; init; }

        public static TempDir Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rig-layout-{Guid.NewGuid():n}");
            Directory.CreateDirectory(path);
            return new TempDir { Path = path };
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best-effort temp cleanup
            }

            return ValueTask.CompletedTask;
        }
    }
}
