using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

if (args.Length == 0)
{
    return 2;
}

switch (args[0])
{
    case "echo-arguments":
        await Console.Out.WriteAsync(JsonSerializer.Serialize(args[1..]));
        return 0;
    case "print-environment":
        await Console.Out.WriteAsync(JsonSerializer.Serialize(args[1..].ToDictionary(
            item => item,
            global::System.Environment.GetEnvironmentVariable,
            StringComparer.Ordinal)));
        return 0;
    case "flood":
        await WriteBytesAsync(Console.OpenStandardOutput(), int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
        await WriteBytesAsync(Console.OpenStandardError(), int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    case "sleep":
        await Task.Delay(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    case "spawn-child":
        return await SpawnChildAsync(args[1], args[2]);
    case "write-after-delay":
        await Task.Delay(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(args[1], "child-survived");
        return 0;
    case "write-file":
        await File.WriteAllTextAsync(args[1], args[2]);
        return 0;
    case "current-directory":
        await Console.Out.WriteAsync(Directory.GetCurrentDirectory());
        return 0;
    case "exit":
        return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    default:
        return 3;
}

static async Task WriteBytesAsync(Stream stream, int count)
{
    var buffer = Enumerable.Repeat((byte)'x', 4096).ToArray();
    while (count > 0)
    {
        var length = Math.Min(count, buffer.Length);
        await stream.WriteAsync(buffer.AsMemory(0, length));
        await stream.FlushAsync();
        count -= length;
    }
}

static async Task<int> SpawnChildAsync(string sentinelPath, string delayText)
{
    var processPath = global::System.Environment.ProcessPath
        ?? throw new InvalidOperationException("Process path is unavailable.");
    var assemblyPath = Assembly.GetExecutingAssembly().Location;
    var startInfo = new ProcessStartInfo
    {
        FileName = processPath,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    };
    foreach (var argument in new[] { assemblyPath, "write-after-delay", sentinelPath, delayText })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var child = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Child process could not be started.");
    await Console.Out.WriteAsync(child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Console.Out.FlushAsync();
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}
