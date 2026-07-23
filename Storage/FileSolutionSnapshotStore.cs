using System.IO.Compression;
using System.Collections.Concurrent;
using System.Text.Json;
using AlchemyProxy.Infrastructure;
using AlchemyProxy.Models;
using Microsoft.Extensions.Options;

namespace AlchemyProxy.Storage;

public sealed class FileSolutionSnapshotStore
{
    private readonly string _rootPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public FileSolutionSnapshotStore(
        IOptions<LocalStorageOptions> options,
        IHostEnvironment environment)
    {
        _rootPath = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);
    }

    public async Task SaveAsync(SolutionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = GetPath(snapshot.SolutionId, snapshot.Version, snapshot.Locale);
        await WriteCompressedJsonAsync(path, snapshot, overwrite: false, cancellationToken);
    }

    public async Task UpdateAsync(SolutionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = GetPath(snapshot.SolutionId, snapshot.Version, snapshot.Locale);
        await WriteCompressedJsonAsync(path, snapshot, overwrite: true, cancellationToken);
    }

    public async Task<SolutionGraph?> TryLoadGraphAsync(
        string graphId,
        string locale,
        CancellationToken cancellationToken)
    {
        var path = GetGraphPath(graphId, locale);
        return File.Exists(path)
            ? await ReadCompressedJsonAsync<SolutionGraph>(path, cancellationToken)
            : null;
    }

    public Task SaveGraphAsync(
        SolutionGraph graph,
        string locale,
        CancellationToken cancellationToken) =>
        WriteCompressedJsonAsync(GetGraphPath(graph.Id, locale), graph, overwrite: false, cancellationToken);

    public async Task<string?> TryLoadResourceAsync(
        string resourceId,
        string locale,
        CancellationToken cancellationToken)
    {
        var path = GetResourcePath(resourceId, locale);
        return File.Exists(path)
            ? await ReadCompressedJsonAsync<string>(path, cancellationToken)
            : null;
    }

    public Task SaveResourceAsync(
        string resourceId,
        string locale,
        string plainText,
        CancellationToken cancellationToken) =>
        WriteCompressedJsonAsync(
            GetResourcePath(resourceId, locale),
            plainText,
            overwrite: false,
            cancellationToken);

    private async Task WriteCompressedJsonAsync<T>(
        string path,
        T value,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var fileLock = _fileLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!overwrite && File.Exists(path))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var file = File.Create(temporaryPath))
                await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
                {
                    await JsonSerializer.SerializeAsync(gzip, value, _jsonOptions, cancellationToken);
                }

                File.Move(temporaryPath, path, overwrite);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            fileLock.Release();
        }
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

        return await ReadCompressedJsonAsync<SolutionSnapshot>(path, cancellationToken);
    }

    private string GetPath(string solutionId, string version, string locale)
    {
        var safeVersion = version.Replace(':', '-');
        return Path.Combine(_rootPath, "solutions", locale, solutionId, $"{safeVersion}.json.gz");
    }

    private string GetGraphPath(string graphId, string locale) =>
        Path.Combine(_rootPath, "fragments", locale, "graphs", $"{graphId}.json.gz");

    private string GetResourcePath(string resourceId, string locale) =>
        Path.Combine(_rootPath, "fragments", locale, "resources", $"{resourceId}.json.gz");

    private async Task<T> ReadCompressedJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync<T>(gzip, _jsonOptions, cancellationToken)
            ?? throw new ApiException(500, "solution_snapshot_invalid", $"Local data at {path} is invalid.");
    }
}
