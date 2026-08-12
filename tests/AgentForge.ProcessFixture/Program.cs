using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

if (args.Length == 0)
{
    return 2;
}

if (args.Contains("--format=custom", StringComparer.Ordinal))
{
    var fileIndex = Array.IndexOf(args, "--file");
    var databaseIndex = Array.IndexOf(args, "--dbname");
    if (fileIndex < 0 || fileIndex + 1 >= args.Length || databaseIndex < 0 ||
        databaseIndex + 1 >= args.Length ||
        string.IsNullOrEmpty(global::System.Environment.GetEnvironmentVariable("PGPASSWORD")) ||
        args[databaseIndex + 1].Contains("Password", StringComparison.OrdinalIgnoreCase))
        return 4;
    await File.WriteAllBytesAsync(args[fileIndex + 1], "deterministic-postgresql-dump"u8.ToArray());
    return 0;
}

if (args.Contains("--clean", StringComparer.Ordinal))
{
    var databaseIndex = Array.IndexOf(args, "--dbname");
    var dump = args[^1];
    if (databaseIndex < 0 || databaseIndex + 1 >= args.Length || !File.Exists(dump) ||
        string.IsNullOrEmpty(global::System.Environment.GetEnvironmentVariable("PGPASSWORD")) ||
        args[databaseIndex + 1].Contains("Password", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(await File.ReadAllTextAsync(dump), "deterministic-postgresql-dump", StringComparison.Ordinal))
        return 4;
    return 0;
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
    case "write-and-wait":
        await Console.Out.WriteAsync(args[1]);
        await Console.Out.FlushAsync();
        await Task.Delay(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
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
    await Console.Out.WriteLineAsync(child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Console.Out.FlushAsync();
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}
