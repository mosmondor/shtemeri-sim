namespace Shtemeri.Sim.Runtime;

/// <summary>Thrown when an instance has used up its instruction budget for the tick. Fleet code cannot swallow it:
/// every instrumented block (catch blocks included) starts with a meter step that throws again.</summary>
public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException() : base("instruction budget exceeded") { }
}

/// <summary>
/// Approximate instruction meter. The fleet compiler inserts <c>Meter.Step(n)</c> at the start of every block of fleet
/// code, where n estimates the IL instructions of that block's own statements (see <see cref="Hosting.BudgetRewriter"/>).
/// API calls that the server prices separately (line of sight, height, log) are charged by the engine.
/// The state is per thread, so several matches can run in parallel.
/// </summary>
public static class Meter
{
    [ThreadStatic] private static long _used;
    [ThreadStatic] private static long _limit;
    [ThreadStatic] private static bool _armed;

    /// <summary>Starts metering one instance's tick.</summary>
    public static void Begin(long limit) { _used = 0; _limit = limit; _armed = true; }

    /// <summary>Stops metering and returns the amount used.</summary>
    public static long End() { _armed = false; return _used; }

    public static long Used => _used;

    public static void Step(int cost)
    {
        if (!_armed) return;
        _used += cost;
        if (_used > _limit) throw new BudgetExceededException();
    }

    public static void Charge(int cost) => Step(cost);
}
