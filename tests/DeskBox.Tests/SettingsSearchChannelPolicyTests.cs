using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Anti-drift pins for the settings-search store-channel filter: the
/// DirectOnly header keys must keep matching live catalog entries. If the
/// generator stops emitting a key (a XAML HeaderKey rename, a section
/// template split), the Navigation filter would keep hiding a key that no
/// longer exists — harmless — or, worse, the key set would drift from the
/// catalog with no signal; these tests fail loudly on both.
/// </summary>
public sealed class SettingsSearchChannelPolicyTests
{
    [Fact]
    public void DirectOnlyHeaderKeys_AreFrozenAtTheTwoChannelGatedCards()
    {
        Assert.Equal(2, SettingsSearchChannelPolicy.DirectOnlyHeaderKeys.Count);
        Assert.Contains("Settings.AutoStart.Mode.Title", SettingsSearchChannelPolicy.DirectOnlyHeaderKeys);
        Assert.Contains("Settings.About.StoreSupportTitle", SettingsSearchChannelPolicy.DirectOnlyHeaderKeys);
    }

    [Fact]
    public void EveryDirectOnlyHeaderKey_MatchesALiveCatalogEntry()
    {
        Assert.NotEmpty(SettingsSearchCatalog.Entries);
        Assert.All(SettingsSearchChannelPolicy.DirectOnlyHeaderKeys, headerKey =>
            Assert.Contains(SettingsSearchCatalog.Entries, entry =>
                string.Equals(entry.HeaderKey, headerKey, StringComparison.Ordinal)));
    }

    [Fact]
    public void IsHiddenInStore_HidesOnlyTheDirectOnlyKeys()
    {
        foreach (string headerKey in SettingsSearchChannelPolicy.DirectOnlyHeaderKeys)
        {
            Assert.True(SettingsSearchChannelPolicy.IsHiddenInStore(headerKey));
        }

        // Every other catalog entry stays searchable in both channels.
        Assert.DoesNotContain(SettingsSearchCatalog.Entries, entry =>
            !SettingsSearchChannelPolicy.DirectOnlyHeaderKeys.Contains(entry.HeaderKey) &&
            SettingsSearchChannelPolicy.IsHiddenInStore(entry.HeaderKey));
    }
}
