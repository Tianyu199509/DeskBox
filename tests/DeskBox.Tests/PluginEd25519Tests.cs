using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

/// <summary>
/// Ground truth for Ed25519 comes from the Node tooling (scripts/spike):
/// the vectors below were generated with the spike dev seed via
/// node:crypto, and the committed spike package signatures are verified
/// cross-implementation in PluginPackageVerifierTests.
/// </summary>
public sealed class PluginEd25519Tests
{
    private const string PublicKeyBase64 = "R/C3TWwBQNgtms2UI1i/yAWr9HnObV2lK0LQfjcPbZA=";

    public static TheoryData<string, string> ValidVectors => new()
    {
        // empty message
        { "", "znBNT1iKqGung3LERxU2o7P5MZ4u6cxucTvAokItLvBZjR+nGW5SHrd21el20eyLD0DM4Q66WgF1lzCkJh7YBQ==" },
        // "deskbox"
        { "6465736b626f78", "m9vUY77HX7jz6piUy4U5b5lGKckuDDuOcoIB/BYfb+80MIXjGMnPjtETBPAXSnMVAGr7y20qQRpDiXYrSOdkCg==" },
        // {"sorted":1,"test":true}
        { "7b22736f72746564223a312c2274657374223a747275657d", "mCzfHP2K184z908KGWdoxUFGbtEH9I0urShyGHKHHBfXF3FQLDVj0Dep8x0kLP+YVx2encorTrtJOFKrjcWXDA==" }
    };

    [Theory]
    [MemberData(nameof(ValidVectors))]
    public void Verify_AcceptsNodeGeneratedSignatures(string messageHex, string signatureBase64)
    {
        byte[] message = Convert.FromHexString(messageHex);
        byte[] signature = Convert.FromBase64String(signatureBase64);
        byte[] publicKey = Convert.FromBase64String(PublicKeyBase64);

        Assert.True(PluginEd25519.Verify(signature, message, publicKey));
    }

    [Fact]
    public void Verify_RejectsTamperedMessage()
    {
        byte[] signature = Convert.FromBase64String(
            "m9vUY77HX7jz6piUy4U5b5lGKckuDDuOcoIB/BYfb+80MIXjGMnPjtETBPAXSnMVAGr7y20qQRpDiXYrSOdkCg==");
        byte[] publicKey = Convert.FromBase64String(PublicKeyBase64);

        Assert.False(PluginEd25519.Verify(signature, "deskbox!"u8.ToArray(), publicKey));
    }

    [Fact]
    public void Verify_RejectsTamperedSignature()
    {
        byte[] signature = Convert.FromBase64String(
            "m9vUY77HX7jz6piUy4U5b5lGKckuDDuOcoIB/BYfb+80MIXjGMnPjtETBPAXSnMVAGr7y20qQRpDiXYrSOdkCg==");
        signature[0] ^= 1;
        byte[] publicKey = Convert.FromBase64String(PublicKeyBase64);

        Assert.False(PluginEd25519.Verify(signature, "deskbox"u8.ToArray(), publicKey));
    }

    [Fact]
    public void Verify_RejectsWrongKey()
    {
        byte[] signature = Convert.FromBase64String(
            "m9vUY77HX7jz6piUy4U5b5lGKckuDDuOcoIB/BYfb+80MIXjGMnPjtETBPAXSnMVAGr7y20qQRpDiXYrSOdkCg==");
        byte[] wrongKey = new byte[32];

        Assert.False(PluginEd25519.Verify(signature, "deskbox"u8.ToArray(), wrongKey));
    }

    [Fact]
    public void Verify_RejectsMalleableScalar()
    {
        // S >= L: take a valid signature and set the scalar bytes to a
        // value above the group order (0xff... > L).
        byte[] signature = Convert.FromBase64String(
            "m9vUY77HX7jz6piUy4U5b5lGKckuDDuOcoIB/BYfb+80MIXjGMnPjtETBPAXSnMVAGr7y20qQRpDiXYrSOdkCg==");
        signature.AsSpan(32).Fill(0xFF);
        byte[] publicKey = Convert.FromBase64String(PublicKeyBase64);

        Assert.False(PluginEd25519.Verify(signature, "deskbox"u8.ToArray(), publicKey));
    }
}
