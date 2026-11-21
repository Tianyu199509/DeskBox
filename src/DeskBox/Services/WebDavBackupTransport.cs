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

    private enum CollectionProbeState
    {
        Exists,
        Missing,
        Forbidden
    }

    private const int MaxRedirects = 3;
    private static readonly TimeSpan DefaultMetadataTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(5);

    private static readonly HttpClient s_sharedClient = CreateSharedClient();

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
    private readonly Uri _baseUri;
    private readonly TimeSpan _metadataTimeout;
    private readonly AuthenticationHeaderValue? _authorization;

    internal WebDavBackupTransport(
        Options options,
        HttpMessageHandler? handler = null,
        TimeSpan? metadataTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ServerUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The WebDAV server URI must be absolute.", nameof(options));
        }

        _options = options;
        _baseUri = BuildCleanBaseUri(options.ServerUri);
        _metadataTimeout = metadataTimeout ?? DefaultMetadataTimeout;
        _client = handler is null
            ? s_sharedClient
            : ConfigureClient(new HttpClient(handler));
        if (!string.IsNullOrEmpty(options.Username) || !string.IsNullOrEmpty(options.Password))
        {
            _authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{options.Username ?? string.Empty}:{options.Password ?? string.Empty}")));
        }
    }

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Handle redirects manually so WebDAV methods (PROPFIND/MKCOL/PUT)
            // are preserved and Authorization is never leaked cross-origin.
            AllowAutoRedirect = false
        };
        return ConfigureClient(new HttpClient(handler));
    }

    private static HttpClient ConfigureClient(HttpClient client)
    {
        client.Timeout = TransferTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskBox/1.6");
        return client;
    }

    /// <summary>PROPFIND Depth:0 on the server root — verifies reachability and auth.</summary>
    public Task ProbeAsync(CancellationToken cancellationToken = default) =>
        RunWithTimeoutAsync(
            "probe",
            _baseUri.AbsolutePath,
            _metadataTimeout,
            cancellationToken,
            async token =>
            {
                (HttpResponseMessage response, _) = await SendWithRedirectsAsync(
                    uri =>
                    {
                        HttpRequestMessage request = BuildRequest(s_propfind, uri);
                        request.Headers.TryAddWithoutValidation("Depth", "0");
                        request.Content = PropfindContent();
                        return request;
                    },
                    ResolveCollectionUri(string.Empty),
                    HttpCompletionOption.ResponseContentRead,
                    token).ConfigureAwait(false);
                using (response)
                {
                    await EnsureSuccessAsync(response, "probe", _baseUri.AbsolutePath).ConfigureAwait(false);
                }

                return true;
            });

    public Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken cancellationToken = default) =>
        RunWithTimeoutAsync(
            "create directory",
            remoteDirectory,
            _metadataTimeout,
            cancellationToken,
            async token =>
            {
                string[] segments = ParseSafeSegments(remoteDirectory);
                if (segments.Length == 0)
                {
                    return true;
                }

                string fullPath = string.Join('/', segments);
                Uri fullUri = ResolveCollectionUri(fullPath);

                // Fast path: most backups target an existing folder; probing it
                // directly skips MKCOL altogether on providers like pCloud that
                // reject MKCOL on existing collections with HTTP 403.
                if (await ProbeCollectionAsync(fullUri, fullPath, token).ConfigureAwait(false) ==
                    CollectionProbeState.Exists)
                {
                    return true;
                }

                // Create progressively — MKCOL fails with 409 when the parent is absent.
                for (int i = 1; i <= segments.Length; i++)
                {
                    string partialPath = string.Join('/', segments.Take(i));
                    Uri collectionUri = ResolveCollectionUri(partialPath);
                    bool isLeaf = i == segments.Length;

                    if (!isLeaf)
                    {
                        CollectionProbeState parentState = await ProbeCollectionAsync(
                            collectionUri, partialPath, token).ConfigureAwait(false);
                        if (parentState is CollectionProbeState.Exists or CollectionProbeState.Forbidden)
                        {
                            continue;
                        }
                    }

                    (HttpResponseMessage response, _) = await SendWithRedirectsAsync(
                        uri => BuildRequest(s_mkcol, uri),
                        collectionUri,
                        HttpCompletionOption.ResponseContentRead,
                        token).ConfigureAwait(false);
                    using (response)
                    {
                        if (response.StatusCode == HttpStatusCode.MethodNotAllowed ||
                            response.StatusCode == HttpStatusCode.Created ||
                            response.IsSuccessStatusCode)
                        {
                            // 405 = collection already exists (RFC 4918); 201 = created.
                            continue;
                        }

                        if (response.StatusCode is HttpStatusCode.Forbidden
                            or HttpStatusCode.Conflict
                            or HttpStatusCode.BadRequest)
                        {
                            // Non-standard WebDAV gateways (pCloud/SabreDAV/WAF) may answer
                            // MKCOL on an already-present directory with 403/400/409.
                            CollectionProbeState recheck = await ProbeCollectionAsync(
                                collectionUri, partialPath, token).ConfigureAwait(false);
                            if (recheck == CollectionProbeState.Exists)
                            {
                                continue;
                            }

                            if (!isLeaf && response.StatusCode == HttpStatusCode.Forbidden)
                            {
                                continue;
                            }
                        }

                        await EnsureSuccessAsync(response, "create directory", remoteDirectory).ConfigureAwait(false);
                    }
                }

                return true;
            });

    public Task<IReadOnlyList<CloudBackupRemoteEntry>> ListAsync(
        string remoteDirectory,
        CancellationToken cancellationToken = default) =>
        RunWithTimeoutAsync<IReadOnlyList<CloudBackupRemoteEntry>>(
            "list",
            remoteDirectory,
            _metadataTimeout,
            cancellationToken,
            async token =>
            {
                Uri collectionUri = ResolveCollectionUri(remoteDirectory);
                (HttpResponseMessage response, Uri finalUri) = await SendWithRedirectsAsync(
                    uri =>
                    {
                        HttpRequestMessage request = BuildRequest(s_propfind, uri);
                        request.Headers.TryAddWithoutValidation("Depth", "1");
                        request.Content = PropfindContent();
                        return request;
                    },
                    collectionUri,
                    HttpCompletionOption.ResponseContentRead,
                    token).ConfigureAwait(false);

                string body;
                using (response)
                {
                    await EnsureSuccessAsync(response, "list", remoteDirectory).ConfigureAwait(false);
                    body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                }

                return ParseMultistatus(body, finalUri);
            });

    public Task UploadAsync(string remoteFilePath, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        long startPosition = content.CanSeek ? content.Position : 0;
        return RunWithTimeoutAsync(
            "upload",
            remoteFilePath,
            TransferTimeout,
            cancellationToken,
            async token =>
            {
                (HttpResponseMessage response, _) = await SendWithRedirectsAsync(
                    uri =>
                    {
                        if (content.CanSeek)
                        {
                            content.Position = startPosition;
                        }

                        HttpRequestMessage request = BuildRequest(HttpMethod.Put, uri);
                        var streamContent = new StreamContent(new NonClosingStream(content));
                        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                        if (content.CanSeek)
                        {
                            streamContent.Headers.ContentLength = content.Length - startPosition;
                        }

                        request.Content = streamContent;
                        return request;
                    },
                    ResolveFileUri(remoteFilePath),
                    HttpCompletionOption.ResponseContentRead,
                    token,
                    onBeforeRedirectRetry: () =>
                    {
                        if (!content.CanSeek)
                        {
                            throw new CloudBackupTransportException(
                                "WebDAV upload cannot follow redirects with a non-seekable stream.");
                        }

                        content.Position = startPosition;
                    }).ConfigureAwait(false);
                using (response)
                {
                    await EnsureSuccessAsync(response, "upload", remoteFilePath).ConfigureAwait(false);
                }

                return true;
            });
    }

    public Task DownloadAsync(string remoteFilePath, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return RunWithTimeoutAsync(
            "download",
            remoteFilePath,
            TransferTimeout,
            cancellationToken,
            async token =>
            {
                (HttpResponseMessage response, _) = await SendWithRedirectsAsync(
                    uri => BuildRequest(HttpMethod.Get, uri),
                    ResolveFileUri(remoteFilePath),
                    HttpCompletionOption.ResponseHeadersRead,
                    token).ConfigureAwait(false);
                using (response)
                {
                    await EnsureSuccessAsync(response, "download", remoteFilePath).ConfigureAwait(false);
                    await response.Content.CopyToAsync(destination, token).ConfigureAwait(false);
                }

                return true;
            });
    }

    public Task DeleteAsync(string remoteFilePath, CancellationToken cancellationToken = default) =>
        RunWithTimeoutAsync(
            "delete",
            remoteFilePath,
            _metadataTimeout,
            cancellationToken,
            async token =>
            {
                (HttpResponseMessage response, _) = await SendWithRedirectsAsync(
                    uri => BuildRequest(HttpMethod.Delete, uri),
                    ResolveFileUri(remoteFilePath),
                    HttpCompletionOption.ResponseContentRead,
                    token).ConfigureAwait(false);
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return true;
                    }

                    await EnsureSuccessAsync(response, "delete", remoteFilePath).ConfigureAwait(false);
                }

                return true;
            });

    private async Task<CollectionProbeState> ProbeCollectionAsync(
        Uri collectionUri,
        string displayPath,
        CancellationToken effectiveToken)
    {
        (HttpResponseMessage response, _) = await SendWithRedirectsAsync(
            uri =>
            {
                HttpRequestMessage request = BuildRequest(s_propfind, uri);
                request.Headers.TryAddWithoutValidation("Depth", "0");
                request.Content = PropfindContent();
                return request;
            },
            collectionUri,
            HttpCompletionOption.ResponseContentRead,
            effectiveToken).ConfigureAwait(false);

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return CollectionProbeState.Missing;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return CollectionProbeState.Forbidden;
            }

            if (response.StatusCode == HttpStatusCode.MultiStatus)
            {
                string body = await response.Content.ReadAsStringAsync(effectiveToken).ConfigureAwait(false);
                return InspectCollectionProbeBody(body, displayPath);
            }

            if (response.IsSuccessStatusCode)
            {
                return CollectionProbeState.Exists;
            }

            await EnsureSuccessAsync(response, "probe directory", displayPath).ConfigureAwait(false);
            return CollectionProbeState.Missing;
        }
    }

    private static CollectionProbeState InspectCollectionProbeBody(string body, string displayPath)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return CollectionProbeState.Exists;
        }

        XDocument document = LoadMultistatusXml(body);
        List<XElement> responses = DescendantDavElements(document, "response").ToList();
        if (responses.Count == 0)
        {
            return CollectionProbeState.Exists;
        }

        bool sawAnyNotFound = false;
        foreach (XElement response in responses)
        {
            XElement? prop = SelectValidPropElement(response);
            if (prop is null)
            {
                if (HasNotFoundStatus(response))
                {
                    sawAnyNotFound = true;
                }

                continue;
            }

            XElement? resourceType = FirstChildDavElement(prop, "resourcetype");
            if (resourceType is not null && FirstChildDavElement(resourceType, "collection") is null)
            {
                throw new CloudBackupTransportException(
                    $"Remote path '{displayPath}' already exists as a file.");
            }

            return CollectionProbeState.Exists;
        }

        return sawAnyNotFound ? CollectionProbeState.Missing : CollectionProbeState.Exists;
    }

    private async Task<T> RunWithTimeoutAsync<T>(
        string operation,
        string remotePath,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> action)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await action(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CloudBackupTransportException(
                $"WebDAV {operation} timed out for '{remotePath}'.",
                inner: ex);
        }
    }

    private async Task<(HttpResponseMessage Response, Uri FinalUri)> SendWithRedirectsAsync(
        Func<Uri, HttpRequestMessage> requestFactory,
        Uri initialUri,
        HttpCompletionOption completionOption,
        CancellationToken effectiveToken,
        Action? onBeforeRedirectRetry = null)
    {
        Uri currentUri = initialUri;
        for (int hop = 0; ; hop++)
        {
            using HttpRequestMessage request = requestFactory(currentUri);
            HttpResponseMessage response = await _client.SendAsync(
                request,
                completionOption,
                effectiveToken).ConfigureAwait(false);

            if (!IsRedirectStatus(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return (response, currentUri);
            }

            HttpStatusCode statusCode = response.StatusCode;
            if (hop >= MaxRedirects)
            {
                response.Dispose();
                throw new CloudBackupTransportException(
                    "WebDAV server exceeded maximum redirect count.",
                    statusCode);
            }

            Uri nextUri = location.IsAbsoluteUri
                ? location
                : new Uri(currentUri, location);

            if (!IsSameOrigin(currentUri, nextUri))
            {
                response.Dispose();
                throw new CloudBackupTransportException(
                    "WebDAV redirect to a different origin or scheme was refused to protect credentials.",
                    statusCode);
            }

            response.Dispose();
            onBeforeRedirectRetry?.Invoke();
            currentUri = nextUri;
        }
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsSameOrigin(Uri currentUri, Uri nextUri) =>
        string.Equals(currentUri.Scheme, nextUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(currentUri.Host, nextUri.Host, StringComparison.OrdinalIgnoreCase) &&
        currentUri.Port == nextUri.Port;

    private HttpRequestMessage BuildRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (_authorization is not null)
        {
            request.Headers.Authorization = _authorization;
        }

        return request;
    }

    private static Uri BuildCleanBaseUri(Uri serverUri)
    {
        var builder = new UriBuilder(serverUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static string[] ParseSafeSegments(string remotePath)
    {
        string normalized = (remotePath ?? string.Empty).Replace('\\', '/');
        string[] segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new CloudBackupTransportException("Invalid remote path.");
            }
        }

        return segments;
    }

    /// <summary>
    /// Resolves a remote collection path against the server base URI,
    /// always terminating with a trailing slash per RFC 4918 §5.2.
    /// </summary>
    private Uri ResolveCollectionUri(string remotePath)
    {
        string[] segments = ParseSafeSegments(remotePath);
        if (segments.Length == 0)
        {
            return _baseUri;
        }

        string escaped = string.Join('/', segments.Select(Uri.EscapeDataString));
        return new Uri(_baseUri, escaped + "/");
    }

    /// <summary>
    /// Resolves a remote file path against the server base URI,
    /// escaping each segment so spaces and non-ASCII names survive.
    /// </summary>
    private Uri ResolveFileUri(string remotePath)
    {
        string[] segments = ParseSafeSegments(remotePath);
        if (segments.Length == 0)
        {
            throw new CloudBackupTransportException("Invalid remote file path.");
        }

        string escaped = string.Join('/', segments.Select(Uri.EscapeDataString));
        return new Uri(_baseUri, escaped);
    }

    private static StringContent PropfindContent() =>
        new(PropfindBody, Encoding.UTF8, "application/xml");

    private static Task EnsureSuccessAsync(HttpResponseMessage response, string operation, string remotePath)
    {
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        string detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized
                => "authentication failed — check the username and app password",
            HttpStatusCode.Forbidden
                => "access denied by server (HTTP 403) — check folder permissions, WebDAV plan eligibility, or app password",
            HttpStatusCode.NotFound => "remote path not found",
            HttpStatusCode.Conflict => "remote path conflict (HTTP 409)",
            HttpStatusCode.InsufficientStorage => "insufficient storage on the server",
            _ => $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
        };
        throw new CloudBackupTransportException(
            $"WebDAV {operation} failed for '{remotePath}': {detail}.",
            response.StatusCode);
    }

    private static XDocument LoadMultistatusXml(string body)
    {
        try
        {
            // The response is server-controlled input: forbid DTDs outright
            // rather than relying on the default null resolver.
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit
            };
            using var reader = System.Xml.XmlReader.Create(new StringReader(body), settings);
            return XDocument.Load(reader);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CloudBackupTransportException("The server returned an invalid PROPFIND response.", inner: ex);
        }
    }

    /// <summary>
    /// Parses a DAV:multistatus document. The first response normally
    /// describes the collection itself and is skipped by comparing the
    /// decoded href path with the requested directory path.
    /// </summary>
    private static List<CloudBackupRemoteEntry> ParseMultistatus(string body, Uri effectiveCollectionUri)
    {
        var entries = new List<CloudBackupRemoteEntry>();
        XDocument document = LoadMultistatusXml(body);

        string requestedDecodedPath = Uri.UnescapeDataString(effectiveCollectionUri.AbsolutePath).TrimEnd('/');
        int responseIndex = -1;

        foreach (XElement response in DescendantDavElements(document, "response"))
        {
            responseIndex++;
            string? href = FirstChildDavElement(response, "href")?.Value?.Trim();
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            string rawPath = ExtractRawPath(href);
            string decodedFullPath = Uri.UnescapeDataString(rawPath).TrimEnd('/');
            if (string.Equals(decodedFullPath, requestedDecodedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            XElement? prop = SelectValidPropElement(response);
            if (prop is null)
            {
                continue;
            }

            XElement? resourceType = FirstChildDavElement(prop, "resourcetype");
            bool isCollection = resourceType is not null &&
                FirstChildDavElement(resourceType, "collection") is not null;

            // Split on '/' BEFORE decoding once so encoded '%' (%25) and
            // encoded '/' (%2F) in a single path segment are never corrupted.
            string trimmedRaw = rawPath.TrimEnd('/');
            int lastSlash = trimmedRaw.LastIndexOf('/');
            string rawLastSegment = lastSlash >= 0 ? trimmedRaw[(lastSlash + 1)..] : trimmedRaw;
            string name = Uri.UnescapeDataString(rawLastSegment);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (responseIndex == 0 &&
                isCollection &&
                requestedDecodedPath.Length > 0 &&
                (requestedDecodedPath.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(requestedDecodedPath.TrimStart('/'), name, StringComparison.OrdinalIgnoreCase)))
            {
                // Reverse proxies may rewrite internal href prefixes so the
                // self-collection path differs in prefix while matching tail.
                continue;
            }

            long? length = long.TryParse(
                FirstChildDavElement(prop, "getcontentlength")?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long parsed)
                ? parsed
                : null;
            DateTimeOffset? lastModified = DateTimeOffset.TryParse(
                FirstChildDavElement(prop, "getlastmodified")?.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset modified)
                ? modified
                : null;

            entries.Add(new CloudBackupRemoteEntry(name, length, lastModified, isCollection));
        }

        return entries;
    }

    private static string ExtractRawPath(string href)
    {
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(href, UriKind.Absolute, out Uri? hrefUri)
                ? hrefUri.AbsolutePath
                : href;
        }

        int queryIndex = href.IndexOfAny(['?', '#']);
        return queryIndex >= 0 ? href[..queryIndex] : href;
    }

    private static XElement? SelectValidPropElement(XElement response)
    {
        List<XElement> propstats = ChildDavElements(response, "propstat").ToList();
        if (propstats.Count > 0)
        {
            foreach (XElement propstat in propstats)
            {
                string? statusText = FirstChildDavElement(propstat, "status")?.Value;
                if (IsSuccessHttpStatusText(statusText))
                {
                    return FirstChildDavElement(propstat, "prop");
                }
            }

            if (propstats.Count == 1 && FirstChildDavElement(propstats[0], "status") is null)
            {
                string? responseStatus = FirstChildDavElement(response, "status")?.Value;
                if (string.IsNullOrWhiteSpace(responseStatus) || IsSuccessHttpStatusText(responseStatus))
                {
                    return FirstChildDavElement(propstats[0], "prop");
                }
            }

            return null;
        }

        string? directStatus = FirstChildDavElement(response, "status")?.Value;
        return string.IsNullOrWhiteSpace(directStatus) || IsSuccessHttpStatusText(directStatus)
            ? FirstChildDavElement(response, "prop")
            : null;
    }

    private static bool HasNotFoundStatus(XElement response)
    {
        foreach (XElement statusElement in DescendantDavElements(response, "status"))
        {
            if (TryParseHttpStatusCode(statusElement.Value, out int code) && code == 404)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSuccessHttpStatusText(string? statusText) =>
        TryParseHttpStatusCode(statusText, out int code) && code is >= 200 and < 300;

    private static bool TryParseHttpStatusCode(string? statusText, out int code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(statusText))
        {
            return false;
        }

        foreach (string part in statusText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 3 &&
                int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                code = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool IsDavElement(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrEmpty(element.Name.NamespaceName) ||
         string.Equals(element.Name.NamespaceName, "DAV:", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> DescendantDavElements(XContainer container, string localName) =>
        container.Descendants().Where(e => IsDavElement(e, localName));

    private static IEnumerable<XElement> ChildDavElements(XContainer container, string localName) =>
        container.Elements().Where(e => IsDavElement(e, localName));

    private static XElement? FirstChildDavElement(XContainer container, string localName) =>
        container.Elements().FirstOrDefault(e => IsDavElement(e, localName));

    /// <summary>
    /// Wraps a caller-owned upload stream so disposing <see cref="StreamContent"/>
    /// between redirect hops never closes the underlying stream.
    /// </summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
