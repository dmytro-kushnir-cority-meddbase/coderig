using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rig.Storage.Storage;

// Used only by `dotnet ef dbcontext optimize` at design time
public sealed class RigDbContextDesignTimeFactory : IDesignTimeDbContextFactory<RigDbContext>
{
    public RigDbContext CreateDbContext(string[] args) => new("design-time.db");
}

public sealed class RigDbContext(string databasePath) : DbContext
{
    public DbSet<RunEntity> Runs => Set<RunEntity>();

    public DbSet<EntryPointEntity> EntryPoints => Set<EntryPointEntity>();

    public DbSet<SourceFileEntity> SourceFiles => Set<SourceFileEntity>();

    public DbSet<EffectEntity> Effects => Set<EffectEntity>();

    public DbSet<EffectObservationEntity> EffectObservations => Set<EffectObservationEntity>();

    public DbSet<DiRegistrationEntity> DiRegistrations => Set<DiRegistrationEntity>();

    public DbSet<MethodObservationEntity> MethodObservations => Set<MethodObservationEntity>();

    public DbSet<InvocationObservationEntity> InvocationObservations => Set<InvocationObservationEntity>();

    public DbSet<CallGraphEntity> CallGraphs => Set<CallGraphEntity>();

    public DbSet<CallGraphNodeEntity> CallGraphNodes => Set<CallGraphNodeEntity>();

    public DbSet<CallGraphNodeCallEntity> CallGraphNodeCalls => Set<CallGraphNodeCallEntity>();

    public DbSet<CallGraphBoundaryCallEntity> CallGraphBoundaryCalls => Set<CallGraphBoundaryCallEntity>();

    public DbSet<CallGraphNodeEffectEntity> CallGraphNodeEffects => Set<CallGraphNodeEffectEntity>();

    public DbSet<SymbolIndexEntity> SymbolIndex => Set<SymbolIndexEntity>();

    public DbSet<SymbolFactEntity> SymbolFacts => Set<SymbolFactEntity>();

    public DbSet<ReferenceFactEntity> ReferenceFacts => Set<ReferenceFactEntity>();

    public DbSet<TypeRelationFactEntity> TypeRelationFacts => Set<TypeRelationFactEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={databasePath}");
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

        modelBuilder.Entity<SymbolIndexEntity>(entity =>
        {
            entity.ToTable("symbol_index");
            entity.HasKey(s => new { s.ProjectIdentity, s.Symbol });
            entity.HasIndex(s => s.Symbol);
            entity.HasIndex(s => s.ProjectIdentity);
        });

        modelBuilder.Entity<EntryPointEntity>(entity =>
        {
            entity.ToTable("entrypoints");
            entity.HasKey(entryPoint => new { entryPoint.RunId, entryPoint.EntryPointIndex });
            entity.HasIndex(entryPoint => new { entryPoint.RunId, entryPoint.DisplayName });
        });

        modelBuilder.Entity<SourceFileEntity>(entity =>
        {
            entity.ToTable("source_files");
            entity.HasKey(sourceFile => new { sourceFile.RunId, sourceFile.FileIndex });
            entity.HasIndex(sourceFile => new { sourceFile.RunId, sourceFile.FilePath });
            entity.HasIndex(sourceFile => new { sourceFile.RunId, sourceFile.Status });
        });

        modelBuilder.Entity<EffectEntity>(entity =>
        {
            entity.ToTable("effects");
            entity.HasKey(effect => new { effect.RunId, effect.EffectIndex });
            entity.HasIndex(effect => new
            {
                effect.RunId,
                effect.Provider,
                effect.Operation,
            });
        });

        modelBuilder.Entity<EffectObservationEntity>(entity =>
        {
            entity.ToTable("effect_observations");
            entity.HasKey(observation => new
            {
                observation.RunId,
                observation.EffectIndex,
                observation.ObservationIndex,
            });
            entity.HasIndex(observation => new { observation.RunId, observation.Type });
        });

        modelBuilder.Entity<DiRegistrationEntity>(entity =>
        {
            entity.ToTable("di_registrations");
            entity.HasKey(registration => new { registration.RunId, registration.RegistrationIndex });
            entity.HasIndex(registration => new { registration.RunId, registration.ServiceType });
            entity.HasIndex(registration => new { registration.RunId, registration.ImplementationType });
        });

        modelBuilder.Entity<MethodObservationEntity>(entity =>
        {
            entity.ToTable("method_observations");
            entity.HasKey(observation => new { observation.RunId, observation.MethodIndex });
            entity.HasIndex(observation => new { observation.RunId, observation.Symbol });
            entity.HasIndex(observation => new { observation.RunId, observation.DisplayName });
        });

        modelBuilder.Entity<InvocationObservationEntity>(entity =>
        {
            entity.ToTable("invocation_observations");
            entity.HasKey(observation => new { observation.RunId, observation.InvocationIndex });
            entity.HasIndex(observation => new { observation.RunId, observation.ContainingMethodSymbol });
            entity.HasIndex(observation => new { observation.RunId, observation.TargetSymbol });
        });

        modelBuilder.Entity<CallGraphEntity>(entity =>
        {
            entity.ToTable("callgraphs");
            entity.HasKey(graph => new { graph.RunId, graph.GraphIndex });
            entity.HasIndex(graph => new { graph.RunId, graph.EntryPoint });
        });

        modelBuilder.Entity<CallGraphNodeEntity>(entity =>
        {
            entity.ToTable("callgraph_nodes");
            entity.HasKey(node => new
            {
                node.RunId,
                node.GraphIndex,
                node.NodeIndex,
            });
            entity.HasIndex(node => new { node.RunId, node.Symbol });
        });

        modelBuilder.Entity<CallGraphNodeCallEntity>(entity =>
        {
            entity.ToTable("callgraph_node_calls");
            entity.HasKey(call => new
            {
                call.RunId,
                call.GraphIndex,
                call.NodeIndex,
                call.CallIndex,
            });
        });

        modelBuilder.Entity<CallGraphBoundaryCallEntity>(entity =>
        {
            entity.ToTable("callgraph_boundary_calls");
            entity.HasKey(call => new
            {
                call.RunId,
                call.GraphIndex,
                call.NodeIndex,
                call.BoundaryCallIndex,
            });
            entity.HasIndex(call => new { call.RunId, call.Kind });
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

        modelBuilder.Entity<CallGraphNodeEffectEntity>(entity =>
        {
            entity.ToTable("callgraph_node_effects");
            entity.HasKey(link => new
            {
                link.RunId,
                link.GraphIndex,
                link.NodeIndex,
                link.LinkIndex,
            });
            entity.HasIndex(link => new
            {
                link.RunId,
                link.GraphIndex,
                link.NodeIndex,
            });
        });
    }
}
