using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

public class NativePackageReloadPolicyTests
{
    private static readonly NativePackageIdentity Identity = new("publisher-a", "deskbox.reload");

    [Fact]
    public void DormantPackageReusesSameHashAndHandleWithoutLoadingAgain()
    {
        var registry = new NativePackageModuleRegistry();
        int loads = 0;
        nint Load() { loads++; return (nint)0x1234; }

        Assert.True(registry.TryLoad(Identity, "hash-a", Load, out nint first));
        // Sessions can come and go; the process-owned registry remains.
        Assert.True(registry.TryLoad(Identity, "hash-a", Load, out nint second));
        Assert.Equal(first, second);
        Assert.Equal((nint)0x1234, second);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void LoadedPackageRejectsDifferentHashWithoutCallingLoader()
    {
        var registry = new NativePackageModuleRegistry();
        Assert.True(registry.TryLoad(Identity, "hash-a", () => (nint)1, out _));

        Assert.False(registry.IsContentHashCompatible(Identity, "hash-b"));
        Assert.False(registry.TryLoad(Identity, "hash-b",
            () => throw new InvalidOperationException("Must not load another binary"), out nint rejected));
        Assert.Equal((nint)0, rejected);
        Assert.True(registry.IsContentHashCompatible(Identity, "hash-a"));
    }

    [Theory]
    [InlineData("missing export")]
    [InlineData("incompatible ABI")]
    [InlineData("activate failed")]
    public void FailureAfterNativeLoadKeepsHashPinned(string failure)
    {
        var registry = new NativePackageModuleRegistry();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            Assert.True(registry.TryLoad(Identity, "hash-a", () => (nint)1, out _));
            // Model session opening failing after its native load returned.
            // No successful session is needed to retain the hash lock.
            throw new InvalidOperationException(failure);
        }));

        Assert.False(registry.TryLoad(Identity, "hash-b",
            () => throw new InvalidOperationException("Must not load another binary"), out _));
        Assert.True(registry.TryLoad(Identity, "hash-a",
            () => throw new InvalidOperationException("Must reuse resident module"), out nint module));
        Assert.Equal((nint)1, module);
    }

    [Fact]
    public void NativeLoadExceptionReleasesReservationForAnotherHash()
    {
        var registry = new NativePackageModuleRegistry();
        Assert.Throws<DllNotFoundException>(() => registry.TryLoad(Identity, "hash-a",
            () => throw new DllNotFoundException(), out _));

        Assert.True(registry.IsContentHashCompatible(Identity, "hash-b"));
        Assert.True(registry.TryLoad(Identity, "hash-b", () => (nint)2, out nint module));
        Assert.Equal((nint)2, module);
        Assert.False(registry.IsContentHashCompatible(Identity, "hash-a"));
    }

    [Fact]
    public void NullHandleDoesNotPinHash()
    {
        var registry = new NativePackageModuleRegistry();
        Assert.False(registry.TryLoad(Identity, "hash-a", () => (nint)0, out _));
        Assert.True(registry.TryLoad(Identity, "hash-b", () => (nint)2, out _));
    }

    [Theory]
    [InlineData("publisher-a", "deskbox.other")]
    [InlineData("publisher-b", "deskbox.reload")]
    public void DifferentPackageIdentitiesDoNotBlockEachOther(string publisher, string packageId)
    {
        var registry = new NativePackageModuleRegistry();
        var other = new NativePackageIdentity(publisher, packageId);
        Assert.True(registry.TryLoad(Identity, "hash-a", () => (nint)1, out _));
        Assert.True(registry.TryLoad(other, "hash-b", () => (nint)2, out nint module));
        Assert.Equal((nint)2, module);
        Assert.False(registry.IsContentHashCompatible(Identity, "hash-b"));
        Assert.False(registry.IsContentHashCompatible(other, "hash-a"));
    }

    [Theory]
    [InlineData("hash-a")]
    [InlineData("hash-b")]
    public void InFlightLoadRejectsReentrantLoadOfSameIdentity(string nestedHash)
    {
        var registry = new NativePackageModuleRegistry();
        Assert.True(registry.TryLoad(Identity, "hash-a", () =>
        {
            Assert.False(registry.TryLoad(Identity, nestedHash,
                () => throw new InvalidOperationException("Must not enter native loader twice"), out _));
            var other = new NativePackageIdentity("publisher-a", "deskbox.other");
            Assert.True(registry.TryLoad(other, "hash-b", () => (nint)2, out _));
            return (nint)1;
        }, out _));
        Assert.True(registry.IsContentHashCompatible(Identity, "hash-a"));
        Assert.False(registry.IsContentHashCompatible(Identity, "hash-b"));
    }
}
