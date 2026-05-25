using Rig.Analysis;

namespace Rig.Cli.Rendering;

internal static class SourceFileRenderer
{
    public static void RenderSkipped(IReadOnlyList<SourceFileInfo> sourceFiles, TextWriter output)
    {
        output.WriteLine("Skipped Files");
        foreach (var sourceFile in sourceFiles)
        {
            output.WriteLine($"  {Path.GetFileName(sourceFile.FilePath)}");
            output.WriteLine($"    project={sourceFile.ProjectName} conf={sourceFile.Confidence} basis={sourceFile.Basis} reason={sourceFile.Reason}");
            output.WriteLine($"    path={sourceFile.FilePath}");
        }
    }
}
