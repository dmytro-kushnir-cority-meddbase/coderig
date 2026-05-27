namespace Rig.Analysis.Rules;

public static class AnalysisProfileValidator
{
    public static void ValidateForSolution(string solutionPath)
    {
        _ = AnalysisRuleSet.LoadForSolution(solutionPath);
    }
}
