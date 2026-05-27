namespace Rig.Analysis.Analysis.Rules;

public static class AnalysisProfileValidator
{
    public static void ValidateForSolution(string solutionPath)
    {
        _ = AnalysisRuleSet.LoadForSolution(solutionPath);
    }
}
