using System.Net;

namespace DeskBox.Services;

/// <summary>
/// Transport seam for cloud-backup providers (roadmap §10): moves already-
/// built scoped archive zips between the local disk and a remote location.
/// Provider #1 is WebDAV; the official DeskBox cloud later implements the
/// same surface — orchestration, credentials and settings stay unchanged.
/// Implementations never see settings.json and never handle credentials
/// beyond what their constructor was given.
/// </summary>
internal interface ICloudBackupTransport
{
    /// <summary>
    /// Verifies connectivity, authentication and protocol support against
    /// the configured endpoint. Throws <see cref="CloudBackupTransportException"/>
    /// on any failure.
    /// </summary>
    Task ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the remote directory (and missing parents) if absent.</summary>
    Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken cancellationToken = default);

    /// <summary>Lists the immediate children of a remote directory.</summary>
    Task<IReadOnlyList<CloudBackupRemoteEntry>> ListAsync(
        string remoteDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Uploads the stream to the remote file path, replacing any existing file.</summary>
    Task UploadAsync(string remoteFilePath, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Streams the remote file into the caller-owned destination.</summary>
    Task DownloadAsync(string remoteFilePath, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>Deletes the remote file; a missing file is not an error.</summary>
    Task DeleteAsync(string remoteFilePath, CancellationToken cancellationToken = default);
}

/// <summary>One entry in a remote backup directory listing.</summary>
internal sealed record CloudBackupRemoteEntry(
    string Name,
    long? Length,
    DateTimeOffset? LastModified,
    bool IsCollection);

/// <summary>
/// Transport failure normalized for the UI layer: carries the HTTP status
/// when one exists and never echoes credentials in the message.
/// </summary>
internal sealed class CloudBackupTransportException : Exception
{
    internal CloudBackupTransportException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    internal HttpStatusCode? StatusCode { get; }
}
