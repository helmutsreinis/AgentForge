using System.Diagnostics;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using Microsoft.Extensions.Options;

namespace AgentForge.Security;

internal sealed class LinuxSecretServiceStore(
    IIdentifierGenerator identifiers,
    IOptions<SecurityOptions> options) : ISecretStore
{
    public const string Name = "linux-secret-service";
    private const int MaximumDiagnosticCharacters = 4096;

    public string StoreName => Name;

    public SecretStoreCapability GetCapability()
    {
        var executable = FindExecutable();
        return executable is null
            ? new SecretStoreCapability(Name, false, new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Linux Secret Service requires the secret-tool executable."))
            : new SecretStoreCapability(Name, true, null);
    }

    public async Task<DomainResult<SecretReference>> StoreAsync(
        string logicalName,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logicalName) || secret.IsEmpty || secret.Length > options.Value.MaximumSecretCharacters)
        {
            return Invalid<SecretReference>("Secret name and bounded secret content are required.");
        }

        var key = identifiers.NewGuid().ToString("D");
        var execution = await ExecuteAsync(
            ["store", "--label=AgentForge", "agentforge-id", key],
            secret,
            captureOutput: false,
            cancellationToken);
        return execution.IsSuccess
            ? DomainResult.Success(new SecretReference(Name, key))
            : DomainResult.Fail<SecretReference>(execution.Failure!);
    }

    public async Task<DomainResult<SecretLease>> MaterializeAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var referenceFailure = ValidateReference(secretReference);
        if (referenceFailure is not null)
        {
            return DomainResult.Fail<SecretLease>(referenceFailure);
        }

        var execution = await ExecuteAsync(
            ["lookup", "agentforge-id", secretReference.Key],
            ReadOnlyMemory<char>.Empty,
            captureOutput: true,
            cancellationToken);
        if (!execution.IsSuccess)
        {
            return DomainResult.Fail<SecretLease>(execution.Failure!);
        }

        var value = execution.Value;
        var length = value.Length;
        while (length > 0 && value[length - 1] is '\r' or '\n')
        {
            length--;
        }

        if (length != value.Length)
        {
            Array.Resize(ref value, length);
        }

        return DomainResult.Success(new SecretLease(value));
    }

    public async Task<DomainResult<bool>> DeleteAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var referenceFailure = ValidateReference(secretReference);
        if (referenceFailure is not null)
        {
            return DomainResult.Fail<bool>(referenceFailure);
        }

        var execution = await ExecuteAsync(
            ["clear", "agentforge-id", secretReference.Key],
            ReadOnlyMemory<char>.Empty,
            captureOutput: false,
            cancellationToken);
        return execution.IsSuccess
            ? DomainResult.Success(true)
            : DomainResult.Fail<bool>(execution.Failure!);
    }

    private async Task<DomainResult<char[]>> ExecuteAsync(
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<char> standardInput,
        bool captureOutput,
        CancellationToken cancellationToken)
    {
        var executable = FindExecutable();
        if (executable is null)
        {
            return DomainResult.Fail<char[]>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Linux Secret Service is unavailable because secret-tool was not found."));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var allowedEnvironment = new[] { "DBUS_SESSION_BUS_ADDRESS", "XDG_RUNTIME_DIR" };
        startInfo.Environment.Clear();
        foreach (var name in allowedEnvironment)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return External<char[]>("Linux Secret Service could not be started.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (!standardInput.IsEmpty)
            {
                await process.StandardInput.WriteAsync(standardInput, timeout.Token);
            }

            process.StandardInput.Close();
            var maximumOutput = captureOutput ? options.Value.MaximumSecretCharacters + 2 : 1;
            var outputTask = ReadBoundedAsync(process.StandardOutput, maximumOutput, timeout.Token);
            var diagnosticTask = ReadBoundedAsync(process.StandardError, MaximumDiagnosticCharacters, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var diagnostic = await diagnosticTask;
            Array.Clear(diagnostic);
            if (process.ExitCode != 0)
            {
                Array.Clear(output);
                return External<char[]>("Linux Secret Service rejected the operation.");
            }

            return DomainResult.Success(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return External<char[]>("Linux Secret Service timed out.");
        }
        catch (IOException)
        {
            TryKill(process);
            return External<char[]>("Linux Secret Service communication failed.");
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }
    }

    private static async Task<char[]> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[maximumCharacters + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > maximumCharacters)
        {
            Array.Clear(buffer);
            throw new IOException("Secret Service output exceeded its bound.");
        }

        Array.Resize(ref buffer, length);
        return buffer;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string? FindExecutable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return File.Exists("/usr/bin/secret-tool") ? "/usr/bin/secret-tool"
            : File.Exists("/bin/secret-tool") ? "/bin/secret-tool"
            : null;
    }

    private static DomainFailure? ValidateReference(SecretReference secretReference)
    {
        if (!string.Equals(secretReference.Store, Name, StringComparison.Ordinal) ||
            !Guid.TryParseExact(secretReference.Key, "D", out var key) || key == Guid.Empty)
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Secret reference does not belong to this store.");
        }

        return null;
    }

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> External<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.RecoverableExternalFailure, message, IsRetryable: true));
}
