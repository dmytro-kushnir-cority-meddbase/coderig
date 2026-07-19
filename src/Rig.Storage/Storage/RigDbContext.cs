using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rig.Storage.Storage;

// Used only by `dotnet ef dbcontext optimize` at design time
public sealed class RigDbContextDesignTimeFactory : IDesignTimeDbContextFactory<RigDbContext>
{
    public RigDbContext CreateDbContext(string[] args) => new("design-time.db");
}

// pooling: when false the connection is opened with Pooling=False, so disposing the context releases
// the underlying file handle immediately. The write-to-temp-then-rename publish (rig index) needs
// this — a pooled handle can keep rig.db.tmp open past Dispose and make the atomic File.Move fail.
//
// readOnly: opens the main DB with Mode=ReadOnly so SQLite physically rejects any write to it — the
// query commands (callers/reaches/tree/path/dead/symbols/refs/di/runs/files) pass readOnly:true, making
// the read/write split an engine-enforced invariant rather than a convention. SqlReachability's TEMP
// tables (reach_set/reach_depth) still work: SQLite's temp database is separate and stays writable on a
// read-only main connection. Only the writers (index, mine, graph) open read-write.
public sealed class RigDbContext(string databasePath, bool pooling = true, bool readOnly = false) : DbContext
{
    public DbSet<RunEntity> Runs => Set<RunEntity>();

    public DbSet<SourceFileEntity> SourceFiles => Set<SourceFileEntity>();

    public DbSet<DiRegistrationEntity> DiRegistrations => Set<DiRegistrationEntity>();

    public DbSet<SymbolFactEntity> SymbolFacts => Set<SymbolFactEntity>();

    public DbSet<ReferenceFactEntity> ReferenceFacts => Set<ReferenceFactEntity>();

    public DbSet<TypeRelationFactEntity> TypeRelationFacts => Set<TypeRelationFactEntity>();

    public DbSet<DispatchFactEntity> DispatchFacts => Set<DispatchFactEntity>();

    public DbSet<AllocationFactEntity> AllocationFacts => Set<AllocationFactEntity>();

    public DbSet<AssemblyEntity> Assemblies => Set<AssemblyEntity>();

    public DbSet<SolutionMembershipEntity> SolutionMemberships => Set<SolutionMembershipEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var connectionString = $"Data Source={databasePath}";
        if (!pooling)
        {
            connectionString += ";Pooling=False";
        }

        if (readOnly)
        {
            connectionString += ";Mode=ReadOnly";
        }

        optionsBuilder.UseSqlite(connectionString);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunEntity>(entity =>
        {
            entity.ToTable("runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Id).ValueGeneratedNever();
            entity.Property(run => run.SolutionPath).IsRequired();
            entity.HasIndex(run => run.CreatedAtUtcText);
            entity.HasIndex(run => run.ProjectIdentity);
        });

        modelBuilder.Entity<SourceFileEntity>(entity =>
        {
            entity.ToTable("source_files");
            entity.HasKey(sourceFile => new { sourceFile.RunId, sourceFile.FileIndex });
            entity.HasIndex(sourceFile => new { sourceFile.RunId, sourceFile.FilePath });
            entity.HasIndex(sourceFile => new { sourceFile.RunId, sourceFile.Status });
        });

        modelBuilder.Entity<DiRegistrationEntity>(entity =>
        {
            entity.ToTable("di_registrations");
            entity.HasKey(registration => new { registration.RunId, registration.RegistrationIndex });
            entity.HasIndex(registration => new { registration.RunId, registration.ServiceType });
            entity.HasIndex(registration => new { registration.RunId, registration.ImplementationType });
        });

        modelBuilder.Entity<SymbolFactEntity>(entity =>
        {
            entity.ToTable("symbol_facts");
            entity.HasKey(s => new { s.RunId, s.SymbolFactIndex });
            // Bare single-column indexes back the random-symbol-lookup query surface: SymbolId (reachability
            // JOIN `SymbolId = r.sym`, EP-deriver StartsWith) and Name (EP-deriver `.Name == ".ctor"`).
            entity.HasIndex(s => s.SymbolId);
            entity.HasIndex(s => s.Name);
            // NO (RunId, SymbolId) composite: the query surface is cross-run / DocID-keyed (see Reads.cs
            // "no latest-run concept") — nothing filters by RunId, so a composite led by RunId can never be
            // seek-used (SymbolId is its second column; the bare SymbolId index serves those lookups). It was
            // pure write + fast-path-rebuild overhead. Re-add a leading-RunId index only if a run-scoped query
            // is introduced.
        });

        modelBuilder.Entity<ReferenceFactEntity>(entity =>
        {
            entity.ToTable("reference_facts");
            entity.HasKey(r => new { r.RunId, r.ReferenceFactIndex });
            // Bare single-column indexes back the hot query surface: EnclosingSymbolId (the reachability
            // recursive-CTE JOIN `r.EnclosingSymbolId = s.sym`) and TargetSymbolId (StartsWith range scans).
            entity.HasIndex(r => r.TargetSymbolId);
            entity.HasIndex(r => r.EnclosingSymbolId);
            // NO (RunId, TargetSymbolId) composite — same reasoning as symbol_facts above. This one sat on
            // the ~1.7M-row reference_facts, so dropping it removes a full-table index rebuild from the save
            // fast-path and shrinks the written store, with zero query impact (no RunId predicate exists).
        });

        modelBuilder.Entity<TypeRelationFactEntity>(entity =>
        {
            entity.ToTable("type_relation_facts");
            entity.HasKey(t => new { t.RunId, t.TypeRelationFactIndex });
            entity.HasIndex(t => t.TypeSymbolId);
            entity.HasIndex(t => t.RelatedSymbolId);
        });

        modelBuilder.Entity<DispatchFactEntity>(entity =>
        {
            entity.ToTable("dispatch_facts");
            entity.HasKey(d => new { d.RunId, d.DispatchFactIndex });
            entity.HasIndex(d => d.SourceMember);
        });

        modelBuilder.Entity<AllocationFactEntity>(entity =>
        {
            entity.ToTable("allocation_facts");
            entity.HasKey(a => new { a.RunId, a.AllocationFactIndex });
            entity.HasIndex(a => a.EnclosingSymbolId);
        });

        modelBuilder.Entity<AssemblyEntity>(entity =>
        {
            entity.ToTable("assemblies");
            entity.HasKey(a => a.AssemblyName);
        });

        modelBuilder.Entity<SolutionMembershipEntity>(entity =>
        {
            entity.ToTable("solution_membership");
            entity.HasKey(m => new { m.SolutionPath, m.AssemblyName });
            entity.HasIndex(m => m.AssemblyName);
        });
    }
}
