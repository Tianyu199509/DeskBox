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
    private const int HrElementNotFound = unchecked((int)0x80070490);

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(Resource, key);
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch (System.IO.FileNotFoundException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            if (ex.HResult != HrElementNotFound)
            {
                App.Log($"[CloudBackup] PasswordVault retrieve failed (hr=0x{ex.HResult:X8}): {ex.Message}");
            }

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
        catch (System.IO.FileNotFoundException)
        {
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            if (ex.HResult != HrElementNotFound)
            {
                App.Log($"[CloudBackup] PasswordVault remove failed (hr=0x{ex.HResult:X8}): {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<string> keys = new PasswordVault()
                .RetrieveAll()
                .Where(credential => string.Equals(
                    credential.Resource, Resource, StringComparison.Ordinal))
                .Select(credential => credential.UserName)
                .ToList();
            return Task.FromResult(keys);
        }
        catch (System.IO.FileNotFoundException)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            if (ex.HResult != HrElementNotFound)
            {
                App.Log($"[CloudBackup] PasswordVault list failed (hr=0x{ex.HResult:X8}): {ex.Message}");
            }

            // An empty vault throws on RetrieveAll.
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }
}
