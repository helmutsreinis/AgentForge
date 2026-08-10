using System.Security.Cryptography;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Artifacts;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentForge.Persistence;

internal sealed class FileSystemArtifactStore(
    AgentForgeDbContext dbContext,
    IDataDirectoryProvider dataDirectoryProvider,
    IOptions<PersistenceOptions> options,
    IClock clock) : IArtifactStore
{
    private const int BufferSize = 81920;

    public async Task<ArtifactReference> PutAsync(
        Stream content,
        string mediaType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        var root = GetArtifactRoot();
        var temporaryRoot = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(temporaryRoot);
        var temporaryPath = Path.Combine(temporaryRoot, $"{Guid.NewGuid():N}.partial");

        long length = 0;
        string hexHash;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                int bytesRead;
                while ((bytesRead = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, bytesRead);
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    length = checked(length + bytesRead);
                }

                await output.FlushAsync(cancellationToken);
            }

            hexHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            var relativePath = Path.Combine("sha256", hexHash[..2], hexHash);
            var destinationPath = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            if (File.Exists(destinationPath))
            {
                File.Delete(temporaryPath);
                if (new FileInfo(destinationPath).Length != length)
                {
                    throw new InvalidDataException("An existing artifact has the expected hash but a different length.");
                }
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }

            var contentHash = $"sha256:{hexHash}";
            var existing = await dbContext.Artifacts.FindAsync([contentHash], cancellationToken);
            if (existing is not null)
            {
                return Map(existing);
            }

            var entity = new ArtifactEntity
            {
                ContentHash = contentHash,
                Length = length,
                MediaType = mediaType,
                CreatedAt = clock.UtcNow,
                RelativePath = relativePath,
            };
            await dbContext.Artifacts.AddAsync(entity, cancellationToken);
            return Map(entity);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<Stream> OpenReadAsync(ArtifactReference artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();

        var hexHash = ParseHash(artifact.ContentHash);
        var path = Path.Combine(GetArtifactRoot(), "sha256", hexHash[..2], hexHash);
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    private string GetArtifactRoot()
    {
        var configuredName = options.Value.ArtifactDirectoryName;
        if (string.IsNullOrWhiteSpace(configuredName) || Path.IsPathRooted(configuredName))
        {
            throw new InvalidOperationException("ArtifactDirectoryName must be a relative directory name.");
        }

        var dataRoot = Path.GetFullPath(dataDirectoryProvider.GetDataDirectory());
        var artifactRoot = Path.GetFullPath(Path.Combine(dataRoot, configuredName));
        if (!artifactRoot.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The artifact directory must remain within the AgentForge data directory.");
        }

        return artifactRoot;
    }

    private static string ParseHash(string contentHash)
    {
        const string prefix = "sha256:";
        if (!contentHash.StartsWith(prefix, StringComparison.Ordinal) || contentHash.Length != prefix.Length + 64)
        {
            throw new ArgumentException("Artifact hash must be a canonical SHA-256 reference.", nameof(contentHash));
        }

        var hexHash = contentHash[prefix.Length..];
        if (hexHash.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new ArgumentException("Artifact hash must use lowercase hexadecimal characters.", nameof(contentHash));
        }

        return hexHash;
    }

    private static ArtifactReference Map(ArtifactEntity entity) => new(
        entity.ContentHash,
        entity.Length,
        entity.MediaType,
        entity.CreatedAt);
}
