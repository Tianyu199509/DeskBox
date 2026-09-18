namespace DeskBox.Services;

/// <summary>
/// OS-credential-backed secret store for cloud-backup provider credentials
/// (roadmap §10 credential discipline: the password never lands in
/// settings.json — only provider/url/path/toggles do).
/// </summary>
internal interface ICredentialStore
{
    /// <summary>Returns the stored secret, or null when absent.</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);

    Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default);

    Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default);
}
