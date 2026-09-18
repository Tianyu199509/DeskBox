using Windows.Security.Credentials;

namespace DeskBox.Services;

/// <summary>
/// Credential store backed by the Windows Credential Locker
/// (<see cref="PasswordVault"/>) — the OS-managed secret surface available
/// to a packaged MSIX app. Secrets are keyed per provider so the same
/// store can later carry the official-cloud session token as well.
/// </summary>
internal sealed class PasswordVaultCredentialStore : ICredentialStore
{
    private const string Resource = "DeskBox/CloudBackup";

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(Resource, key);
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException ||
                                     ex is System.IO.FileNotFoundException)
        {
            // PasswordVault.Retrieve throws (not null) when the key is absent.
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(secret);
        new PasswordVault().Add(new PasswordCredential(Resource, key, secret));
        return Task.CompletedTask;
    }

    public Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            new PasswordVault().Remove(new PasswordCredential(Resource, key, string.Empty));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException ||
                                     ex is System.IO.FileNotFoundException)
        {
            // Removing an absent key is a no-op.
        }

        return Task.CompletedTask;
    }
}
