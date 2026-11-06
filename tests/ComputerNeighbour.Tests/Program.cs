using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Tests;

internal static class TestRunner
{
    public static async Task<int> Main()
    {
        var portable = PortablePaths.Discover([]);
        portable.InitializeEnvironment();
        var runRoot = portable.Inside("data/test-runs/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var suite = new Suite(runRoot);
        await MemoryTests.RunAsync(suite);
        await BehaviorTests.RunAsync(suite);
        await IntentRouterTests.RunAsync(suite);
        await EngineTests.RunAsync(suite);
        Console.WriteLine($"\n{suite.Passed} passed, {suite.Failed} failed. Tests require neither model files nor microphone/desktop actions nor network access.");
        if (suite.Failed != 0)
        {
            Console.WriteLine("Failed-run artifacts retained: " + runRoot);
            return 1;
        }
        var allowedRoot = portable.Inside("data/test-runs") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(runRoot).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup target escaped its workspace.");
        Directory.Delete(runRoot, recursive: true);
        return 0;
    }
}

internal sealed class Suite(string root)
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public async Task Case(string name, Func<string, Task> run)
    {
        var fixture = Path.Combine(root, $"case-{Passed + Failed + 1:D2}");
        Directory.CreateDirectory(fixture);
        try
        {
            await run(fixture).WaitAsync(TimeSpan.FromSeconds(20));
            Passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e)
        {
            Failed++;
            Console.Error.WriteLine("FAIL " + name + ": " + e);
        }
    }
    public Task Case(string name, Action<string> run) => Case(name, p => { run(p); return Task.CompletedTask; });
}

internal static class Check
{
    public static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    public static void Equal<T>(T expected, T actual, string message = "Values differ")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'.");
    }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
