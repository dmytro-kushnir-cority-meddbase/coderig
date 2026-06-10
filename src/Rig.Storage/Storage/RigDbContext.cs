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
public sealed class RigDbContext(string databasePath, bool pooling = true) : DbContext
{
    public DbSet<RunEntity> Runs => Set<RunEntity>();

    public DbSet<SourceFileEntity> SourceFiles => Set<SourceFileEntity>();

    public DbSet<DiRegistrationEntity> DiRegistrations => Set<DiRegistrationEntity>();

    public DbSet<SymbolFactEntity> SymbolFacts => Set<SymbolFactEntity>();

    public DbSet<ReferenceFactEntity> ReferenceFacts => Set<ReferenceFactEntity>();

    public DbSet<TypeRelationFactEntity> TypeRelationFacts => Set<TypeRelationFactEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var connectionString = pooling ? $"Data Source={databasePath}" : $"Data Source={databasePath};Pooling=False";
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
            entity.HasIndex(s => s.SymbolId);
            entity.HasIndex(s => s.Name);
            entity.HasIndex(s => new { s.RunId, s.SymbolId });
        });

        modelBuilder.Entity<ReferenceFactEntity>(entity =>
        {
            entity.ToTable("reference_facts");
            entity.HasKey(r => new { r.RunId, r.ReferenceFactIndex });
            entity.HasIndex(r => r.TargetSymbolId);
            entity.HasIndex(r => r.EnclosingSymbolId);
            entity.HasIndex(r => new { r.RunId, r.TargetSymbolId });
        });

        modelBuilder.Entity<TypeRelationFactEntity>(entity =>
        {
            entity.ToTable("type_relation_facts");
            entity.HasKey(t => new { t.RunId, t.TypeRelationFactIndex });
            entity.HasIndex(t => t.TypeSymbolId);
            entity.HasIndex(t => t.RelatedSymbolId);
        });
    }
}
