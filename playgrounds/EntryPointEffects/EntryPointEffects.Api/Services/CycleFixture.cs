namespace EntryPointEffects.Api.Services;

public static class CycleFixture
{
    public static int SelfRecursive(int remaining)
    {
        return remaining <= 0 ? 0 : SelfRecursive(remaining - 1) + 1;
    }

    public static int MutualA(int remaining)
    {
        return remaining <= 0 ? 0 : MutualB(remaining - 1) + 1;
    }

    public static int MutualB(int remaining)
    {
        return remaining <= 0 ? 0 : MutualA(remaining - 1) + 1;
    }

    public static int ThreeStepA(int remaining)
    {
        return remaining <= 0 ? 0 : ThreeStepB(remaining - 1) + 1;
    }

    public static int ThreeStepB(int remaining)
    {
        return ThreeStepC(remaining);
    }

    public static int ThreeStepC(int remaining)
    {
        return ThreeStepA(remaining);
    }
}
