using System.IO.Compression;
using System.Text.Json;
using AlchemyProxy.Infrastructure;
using AlchemyProxy.Models;
using Microsoft.Extensions.Options;

namespace AlchemyProxy.Storage;

public sealed class FileSolutionSnapshotStore
{
    private readonly string _rootPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public FileSolutionSnapshotStore(
        IOptions<LocalStorageOptions> options,
        IHostEnvironment environment)
    {
        _rootPath = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);
    }

    public async Task SaveAsync(SolutionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = GetPath(snapshot.SolutionId, snapshot.Version, snapshot.Locale);
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        await using (var file = File.Create(temporaryPath))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            await JsonSerializer.SerializeAsync(gzip, snapshot, _jsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: false);
    }

    public async Task<SolutionSnapshot> LoadAsync(
        string solutionId,
        string version,
        string locale,
        CancellationToken cancellationToken)
    {
        var path = GetPath(solutionId, version, locale);
        if (!File.Exists(path))
        {
            throw new ApiException(500, "solution_snapshot_not_found", "The session solution snapshot is missing.");
        }

        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync<SolutionSnapshot>(gzip, _jsonOptions, cancellationToken)
            ?? throw new ApiException(500, "solution_snapshot_invalid", "The session solution snapshot is invalid.");
    }

    private string GetPath(string solutionId, string version, string locale)
    {
        var safeVersion = version.Replace(':', '-');
        return Path.Combine(_rootPath, "solutions", locale, solutionId, $"{safeVersion}.json.gz");
    }
}
