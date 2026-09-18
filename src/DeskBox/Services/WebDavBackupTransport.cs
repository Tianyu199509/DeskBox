using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace DeskBox.Services;

/// <summary>
/// WebDAV implementation of <see cref="ICloudBackupTransport"/> over
/// <see cref="HttpClient"/> — PROPFIND for probe/listing, MKCOL for
/// directory creation, PUT/GET/DELETE for file moves. No new dependencies;
/// the official cloud provider later implements the same interface.
/// </summary>
internal sealed class WebDavBackupTransport : ICloudBackupTransport
{
    internal sealed record Options(Uri ServerUri, string Username, string? Password);

    private static readonly HttpClient s_sharedClient = new()
    {
        // Backup archives carry user attachments; 100s is too tight for
        // slow WebDAV endpoints.
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static readonly XNamespace Dav = "DAV:";

    private static readonly HttpMethod s_propfind = new("PROPFIND");
    private static readonly HttpMethod s_mkcol = new("MKCOL");

    private const string PropfindBody =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <propfind xmlns="DAV:">
          <prop>
            <displayname/>
            <getcontentlength/>
            <getlastmodified/>
            <resourcetype/>
          </prop>
        </propfind>
        """;

    private readonly HttpClient _client;
    private readonly Options _options;
    private readonly AuthenticationHeaderValue? _authorization;

    internal WebDavBackupTransport(Options options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ServerUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The WebDAV server URI must be absolute.", nameof(options));
        }

        _options = options;
        _client = handler is null
            ? s_sharedClient
            : new HttpClient(handler) { Timeout = s_sharedClient.Timeout };
        if (!string.IsNullOrEmpty(options.Username))
        {
            _authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password ?? string.Empty}")));
        }
    }

    /// <summary>PROPFIND Depth:0 on the server root — verifies reachability and auth.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = BuildRequest(s_propfind, _options.ServerUri);
        request.Headers.TryAddWithoutValidation("Depth", "0");
        request.Content = PropfindContent();
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "probe", _options.ServerUri.AbsolutePath);
    }

    public async Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken cancellationToken = default)
    {
        string[] segments = remoteDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        // Create progressively — MKCOL fails with 409 when the parent is absent.
        for (int i = 1; i <= segments.Length; i++)
        {
            Uri uri = ResolveUri(string.Join('/', segments.Take(i)));
            using HttpRequestMessage request = BuildRequest(s_mkcol, uri);
            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed ||
                response.StatusCode == HttpStatusCode.Created ||
                response.IsSuccessStatusCode)
            {
                // 405 = the collection already exists on every mainstream
                // WebDAV server (RFC 4918); 201 = just created.
                continue;
            }

            await EnsureSuccessAsync(response, "create directory", remoteDirectory);
        }
    }

    public async Task<IReadOnlyList<CloudBackupRemoteEntry>> ListAsync(
        string remoteDirectory,
        CancellationToken cancellationToken = default)
    {
        Uri uri = ResolveUri(remoteDirectory);
        using HttpRequestMessage request = BuildRequest(s_propfind, uri);
        request.Headers.TryAddWithoutValidation("Depth", "1");
        request.Content = PropfindContent();

        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "list", remoteDirectory);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseMultistatus(body, uri);
    }

    public async Task UploadAsync(string remoteFilePath, Stream content, CancellationToken cancellationToken = default)
    {
        Uri uri = ResolveUri(remoteFilePath);
        using HttpRequestMessage request = BuildRequest(HttpMethod.Put, uri);
        request.Content = new StreamContent(content);
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "upload", remoteFilePath);
    }

    public async Task DownloadAsync(string remoteFilePath, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Uri uri = ResolveUri(remoteFilePath);
        using HttpRequestMessage request = BuildRequest(HttpMethod.Get, uri);
        using HttpResponseMessage response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "download", remoteFilePath);
        await response.Content.CopyToAsync(destination, cancellationToken);
    }

    public async Task DeleteAsync(string remoteFilePath, CancellationToken cancellationToken = default)
    {
        Uri uri = ResolveUri(remoteFilePath);
        using HttpRequestMessage request = BuildRequest(HttpMethod.Delete, uri);
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, "delete", remoteFilePath);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (_authorization is not null)
        {
            request.Headers.Authorization = _authorization;
        }

        return request;
    }

    /// <summary>
    /// Resolves a slash-separated remote path against the server base URI,
    /// escaping each segment so spaces/unicode survive.
    /// </summary>
    private Uri ResolveUri(string remotePath)
    {
        string escaped = string.Join(
            '/',
            remotePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        Uri baseUri = _options.ServerUri;
        if (!baseUri.AbsolutePath.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.OriginalString + '/');
        }

        return new Uri(baseUri, escaped);
    }

    private static StringContent PropfindContent() =>
        new(PropfindBody, Encoding.UTF8, "application/xml");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, string remotePath)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => "authentication failed — check the username and app password",
            HttpStatusCode.NotFound => "remote path not found",
            HttpStatusCode.InsufficientStorage => "insufficient storage on the server",
            _ => $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
        };
        throw new CloudBackupTransportException(
            $"WebDAV {operation} failed for '{remotePath}': {detail}.",
            response.StatusCode);
    }

    /// <summary>
    /// Parses a DAV:multistatus document. The first response normally
    /// describes the collection itself and is skipped by comparing the
    /// decoded href path with the requested directory path.
    /// </summary>
    private static List<CloudBackupRemoteEntry> ParseMultistatus(string body, Uri requestedUri)
    {
        var entries = new List<CloudBackupRemoteEntry>();
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CloudBackupTransportException("The server returned an invalid PROPFIND response.", inner: ex);
        }

        string requestedPath = requestedUri.AbsolutePath.TrimEnd('/');

        foreach (XElement response in document.Descendants(Dav + "response"))
        {
            string? href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            string decodedPath = Uri.UnescapeDataString(
                href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(href).AbsolutePath
                    : href);
            if (decodedPath.TrimEnd('/').Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only properties inside a 200 propstat are trustworthy.
            XElement? prop = response
                .Elements(Dav + "propstat")
                .Where(ps => (ps.Element(Dav + "status")?.Value.Contains(" 200") ?? false))
                .Select(ps => ps.Element(Dav + "prop"))
                .FirstOrDefault();
            if (prop is null)
            {
                continue;
            }

            bool isCollection = prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            string name = prop.Element(Dav + "displayname")?.Value is { Length: > 0 } displayName
                ? displayName
                : Uri.UnescapeDataString(decodedPath.TrimEnd('/').Split('/').Last());
            long? length = long.TryParse(
                prop.Element(Dav + "getcontentlength")?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long parsed)
                ? parsed
                : null;
            DateTimeOffset? lastModified = DateTimeOffset.TryParse(
                prop.Element(Dav + "getlastmodified")?.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset modified)
                ? modified
                : null;

            entries.Add(new CloudBackupRemoteEntry(name, length, lastModified, isCollection));
        }

        return entries;
    }
}
