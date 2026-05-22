using System.Text.Json;
using Rig.Analysis;

namespace Rig.Cli;

internal sealed class RunStore(string workingDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string storeDirectory = Path.Combine(workingDirectory, ".rig");

    public async Task SaveLatestAsync(AnalysisResult result, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(storeDirectory);

        var latestPath = Path.Combine(storeDirectory, "latest.json");
        await using var stream = File.Create(latestPath);
        await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);
    }

    public async Task<AnalysisResult?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        var latestPath = Path.Combine(storeDirectory, "latest.json");
        if (!File.Exists(latestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(latestPath);
        return await JsonSerializer.DeserializeAsync<AnalysisResult>(stream, JsonOptions, cancellationToken);
    }
}
