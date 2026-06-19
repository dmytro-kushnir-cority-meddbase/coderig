using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Shouldly;

namespace Rig.Tests.Storage;

// The commit-stamp primitive (docs/design-impact-behavioral-diff.md §4.5): the GitProvenance captured at
// `rig index` time must round-trip through Writes.SaveAsync -> the runs table -> Reads.ListRunsAsync, so a
// store is addressable by the commit it was built from. A non-git source (GitProvenance.None) leaves the
// run unstamped (null commit, clean), never failing the index.
public sealed class CommitStampTests
{
    [Test]
    public async Task Source_commit_branch_and_dirty_flag_round_trip_through_a_run()
    {
        await WithTempStoreAsync(
            new GitProvenance(Commit: "abc123def4567890", Branch: "feature/healthcode", Dirty: true),
            runs =>
            {
                runs.Count.ShouldBe(1);
                runs[0].SourceCommit.ShouldBe("abc123def4567890");
                runs[0].SourceBranch.ShouldBe("feature/healthcode");
                runs[0].SourceDirty.ShouldBeTrue();
            }
        );
    }

    [Test]
    public async Task Absent_provenance_leaves_a_run_unstamped()
    {
        await WithTempStoreAsync(
            GitProvenance.None,
            runs =>
            {
                runs.Count.ShouldBe(1);
                runs[0].SourceCommit.ShouldBeNull();
                runs[0].SourceBranch.ShouldBeNull();
                runs[0].SourceDirty.ShouldBeFalse();
            }
        );
    }

    // Write a single run with the given provenance into a throwaway store, then hand the read-back runs to
    // the assertion. SaveAsync creates + migrates the schema itself, so no explicit EnsureCreated is needed.
    private static async Task WithTempStoreAsync(GitProvenance provenance, Action<IReadOnlyList<RunSummary>> assert)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"rig-commitstamp-{Guid.NewGuid():n}.db");
        try
        {
            var result = new AnalysisResult(SolutionPath: "C:/demo/Demo.sln", SourceFiles: [], DiRegistrations: []);

            await using (var write = new RigDbContext(dbPath, pooling: false))
            {
                await Writes.SaveAsync(write, result, provenance: provenance);
            }

            await using var read = new RigDbContext(dbPath, pooling: false);
            assert(await Reads.ListRunsAsync(read));
        }
        finally
        {
            foreach (var sidecar in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
        }
    }
}
