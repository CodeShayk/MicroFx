using System.ComponentModel.DataAnnotations;
using MicroFx.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MicroFx.Storage;

/// <summary>Options for the storage feature, bound from <c>MicroFx:Storage</c>.</summary>
public sealed class StorageOptions
{
    /// <summary>Root directory for the in-box filesystem store.</summary>
    public string RootPath { get; set; } =
        Path.Combine(Path.GetTempPath(), "microfx-storage");

    /// <summary>Maximum object size accepted.</summary>
    [Range(1024, long.MaxValue)]
    public long MaximumObjectBytes { get; set; } = 64 * 1024 * 1024;
}

/// <summary>Metadata about a stored object.</summary>
/// <param name="Key">The object key.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="LastModified">When it was last written.</param>
public readonly record struct ObjectInfo(string Key, long SizeBytes, DateTimeOffset LastModified);

/// <summary>
/// Object storage. The in-box implementation writes to the local filesystem; an adapter such as
/// <c>MicroFx.Aws</c> backs it with S3.
/// </summary>
public interface IObjectStore
{
    /// <summary>Writes an object, replacing any existing one.</summary>
    ValueTask PutAsync(string key, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Opens an object for reading, or returns null when absent.</summary>
    ValueTask<Stream?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Returns metadata, or null when absent.</summary>
    ValueTask<ObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes an object. Succeeds whether or not it existed.</summary>
    ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Filesystem-backed object store for local development and test.
/// </summary>
/// <remarks>
/// Object keys are caller-supplied and therefore a path-traversal vector: a key of
/// <c>../../etc/passwd</c> must not escape the root. Every key is validated and the resolved path is
/// re-checked against the root after normalisation, because validation alone misses symlinks and
/// platform-specific path quirks.
/// </remarks>
internal sealed class FileSystemObjectStore : IObjectStore
{
    private readonly string _root;
    private readonly StorageOptions _options;

    public FileSystemObjectStore(Microsoft.Extensions.Options.IOptions<StorageOptions> options)
    {
        _options = options.Value;
        _root = Path.GetFullPath(_options.RootPath);
        Directory.CreateDirectory(_root);
    }

    public async ValueTask PutAsync(
        string key, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written to a temporary file and moved into place, so a crash mid-write cannot leave a
        // truncated object that a reader would treat as complete.
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using (var file = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await CopyBoundedAsync(content, file, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    public ValueTask<Stream?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);

        return ValueTask.FromResult<Stream?>(
            File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, useAsync: true)
                : null);
    }

    public ValueTask<ObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(ResolvePath(key));

        return ValueTask.FromResult<ObjectInfo?>(
            info.Exists
                ? new ObjectInfo(key, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
                : null);
    }

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    private async Task CopyBoundedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;

            // Enforced while streaming rather than from a declared length, which a caller controls
            // and which is absent on a chunked upload.
            if (total > _options.MaximumObjectBytes)
            {
                throw new InvalidOperationException(
                    $"Object exceeds the {_options.MaximumObjectBytes}-byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Validates a key and maps it to a path guaranteed to sit under the root.</summary>
    private string ResolvePath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key.Length > 1024)
        {
            throw new ArgumentException("Object key exceeds 1024 characters.", nameof(key));
        }

        foreach (var character in key)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/';
            if (!allowed)
            {
                throw new ArgumentException(
                    "Object key may contain only letters, digits, hyphen, underscore, dot, and slash.",
                    nameof(key));
            }
        }

        // Rejects '..' as a whole segment. Checking for the substring would also reject a
        // legitimate key such as 'report..v2', while missing nothing extra.
        foreach (var segment in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is ".." or ".")
            {
                throw new ArgumentException("Object key may not contain relative segments.", nameof(key));
            }
        }

        var resolved = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));

        // Re-checked after normalisation. Validation above should make this unreachable, which is
        // exactly why it is worth keeping: it is the assertion that catches the case we missed.
        if (!resolved.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(resolved, _root, StringComparison.Ordinal))
        {
            throw new ArgumentException("Object key resolves outside the storage root.", nameof(key));
        }

        return resolved;
    }
}

/// <summary>Object storage with a filesystem default and an adapter-supplied production backend.</summary>
public sealed class StorageFeature : IMicroFxFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Storage,
        DisplayName = "Storage",
        Order = 320,
        DependsOn = [BuiltIn.Core],
        EnabledByDefault = false,   // most services store nothing; opting in is explicit
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:Storage",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddValidatedOptions<StorageOptions>();
        context.Services.TryAddSingleton<IObjectStore, FileSystemObjectStore>();

        context.Report("store", "filesystem");
    }
}
