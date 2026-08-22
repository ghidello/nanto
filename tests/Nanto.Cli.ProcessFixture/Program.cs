using System.Diagnostics;

namespace Nanto.Cli.ProcessFixture;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not [var mode, .. var remaining])
        {
            return 2;
        }

        return mode switch
        {
            "exit" => int.Parse(remaining.Single(), System.Globalization.CultureInfo.InvariantCulture),
            "flood" => Flood(),
            "child" => await HoldLockAsync(remaining.Single()),
            "spawn-grandchild" => await SpawnGrandchildAsync(remaining),
            _ => 2,
        };
    }

    private static int Flood()
    {
        for (int index = 0; index < 100; index++)
        {
            Console.WriteLine(index == 99 ? new string('x', 10_000) : $"fixture-line-{index}");
        }

        return 0;
    }

    private static async Task<int> HoldLockAsync(string lockPath)
    {
        await using var stream = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("child-ready");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> SpawnGrandchildAsync(string[] args)
    {
        if (args is not [var processIdPath, var lockPath])
        {
            return 2;
        }

        await Task.Delay(500);
        string assemblyPath = typeof(ProcessFixtureMarker).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("child");
        startInfo.ArgumentList.Add(lockPath);
        using Process child = Process.Start(startInfo) ?? throw new InvalidOperationException("Grandchild did not start.");
        string temporaryProcessIdPath = processIdPath + ".tmp";
        await File.WriteAllTextAsync(temporaryProcessIdPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(temporaryProcessIdPath, processIdPath);
        Console.WriteLine("grandchild-started");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}
