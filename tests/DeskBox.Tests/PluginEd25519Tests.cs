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

    // ----- adversarial public keys / signatures (round 9) -----

    // The base point encoding = [1]B; with S = 1 (little-endian) this is
    // the classic small-order-public-key forge: right = R + h*A collapses
    // to R when A is the identity, so the "signature" verifies for ANY
    // message without any private key.
    private static byte[] ForgedSignature() => Convert.FromHexString(
        "5866666666666666666666666666666666666666666666666666666666666666" + // R = base point
        "0100000000000000000000000000000000000000000000000000000000000000"); // S = 1

    [Fact]
    public void Verify_RejectsIdentityPublicKeyForge()
    {
        byte[] signature = ForgedSignature();
        byte[] identityKey = Convert.FromHexString(
            "0100000000000000000000000000000000000000000000000000000000000000");

        Assert.False(PluginEd25519.Verify(signature, "any message"u8.ToArray(), identityKey));
        Assert.False(PluginEd25519.Verify(signature, "other message"u8.ToArray(), identityKey));
        Assert.False(PluginEd25519.Verify(signature, [], identityKey));
    }

    public static TheoryData<string> SmallOrderPublicKeyHex => new()
    {
        // identity (order 1)
        "0100000000000000000000000000000000000000000000000000000000000000",
        // genuine order-8 point (independently computed as [L] of a generic
        // curve point; also the well-known small-order vector) - this is
        // the case the [4]P-vs-[8]P bug let through (round 10)
        "c7176a703d4dd84fba3c0b760d10670f2a2053fa2c39ccc64ec7fd7792ac037a",
        // order-2 point: y = p-1, x = 0
        "ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
        // 0xff * 32: non-canonical y >= p
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
        // y = 0 with the sign bit set: non-canonical
        "0000000000000000000000000000000000000000000000000000000000000080"
    };

    [Theory]
    [MemberData(nameof(SmallOrderPublicKeyHex))]
    public void Verify_RejectsSmallOrderOrNonCanonicalPublicKeys(string publicKeyHex)
    {
        byte[] signature = ForgedSignature();

        Assert.False(PluginEd25519.Verify(
            signature,
            "any"u8.ToArray(),
            Convert.FromHexString(publicKeyHex)));
    }

    [Fact]
    public void Verify_RejectsSmallOrderRWithLegitimateKey()
    {
        // R = identity (01 00 ...), S = 1, real key: strict verification
        // rejects small-order R outright.
        byte[] signature = new byte[64];
        signature[0] = 0x01; // R = identity encoding
        signature[32] = 0x01; // S = 1

        Assert.False(PluginEd25519.Verify(
            signature,
            "deskbox"u8.ToArray(),
            Convert.FromBase64String(PublicKeyBase64)));
    }

    // ----- RFC 8032 §7.1 official vectors (independent ground truth from
    // the Node-generated dev-key vectors) -----

    public static TheoryData<string, string, string> Rfc8032Vectors => new()
    {
        // TEST 1: empty message
        {
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
        },
        // TEST 2: one byte 0x72
        {
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"
        },
        // TEST 3: two bytes af82
        {
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"
        },
        // TEST 1024: 1023-byte message
        {
            "278117fc144c72340f67d0f2316e8386ceffbf2b2428c9c51fef7c597f1d426e",
            "08b8b2b733424243760fe426a4b54908632110a66c2f6591eabd3345e3e4eb98fa6e264bf09efe12ee50f8f54e9f77b1e355f6c50544e23fb1433ddf73be84d879de7c0046dc4996d9e773f4bc9efe5738829adb26c81b37c93a1b270b20329d658675fc6ea534e0810a4432826bf58c941efb65d57a338bbd2e26640f89ffbc1a858efcb8550ee3a5e1998bd177e93a7363c344fe6b199ee5d02e82d522c4feba15452f80288a821a579116ec6dad2b3b310da903401aa62100ab5d1a36553e06203b33890cc9b832f79ef80560ccb9a39ce767967ed628c6ad573cb116dbefefd75499da96bd68a8a97b928a8bbc103b6621fcde2beca1231d206be6cd9ec7aff6f6c94fcd7204ed3455c68c83f4a41da4af2b74ef5c53f1d8ac70bdcb7ed185ce81bd84359d44254d95629e9855a94a7c1958d1f8ada5d0532ed8a5aa3fb2d17ba70eb6248e594e1a2297acbbb39d502f1a8c6eb6f1ce22b3de1a1f40cc24554119a831a9aad6079cad88425de6bde1a9187ebb6092cf67bf2b13fd65f27088d78b7e883c8759d2c4f5c65adb7553878ad575f9fad878e80a0c9ba63bcbcc2732e69485bbc9c90bfbd62481d9089beccf80cfe2df16a2cf65bd92dd597b0707e0917af48bbb75fed413d238f5555a7a569d80c3414a8d0859dc65a46128bab27af87a71314f318c782b23ebfe808b82b0ce26401d2e22f04d83d1255dc51addd3b75a2b1ae0784504df543af8969be3ea7082ff7fc9888c144da2af58429ec96031dbcad3dad9af0dcbaaaf268cb8fcffead94f3c7ca495e056a9b47acdb751fb73e666c6c655ade8297297d07ad1ba5e43f1bca32301651339e22904cc8c42f58c30c04aafdb038dda0847dd988dcda6f3bfd15c4b4c4525004aa06eeff8ca61783aacec57fb3d1f92b0fe2fd1a85f6724517b65e614ad6808d6f6ee34dff7310fdc82aebfd904b01e1dc54b2927094b2db68d6f903b68401adebf5a7e08d78ff4ef5d63653a65040cf9bfd4aca7984a74d37145986780fc0b16ac451649de6188a7dbdf191f64b5fc5e2ab47b57f7f7276cd419c17a3ca8e1b939ae49e488acba6b965610b5480109c8b17b80e1b7b750dfc7598d5d5011fd2dcc5600a32ef5b52a1ecc820e308aa342721aac0943bf6686b64b2579376504ccc493d97e6aed3fb0f9cd71a43dd497f01f17c0e2cb3797aa2a2f256656168e6c496afc5fb93246f6b1116398a346f1a641f3b041e989f7914f90cc2c7fff357876e506b50d334ba77c225bc307ba537152f3f1610e4eafe595f6d9d90d11faa933a15ef1369546868a7f3a45a96768d40fd9d03412c091c6315cf4fde7cb68606937380db2eaaa707b4c4185c32eddcdd306705e4dc1ffc872eeee475a64dfac86aba41c0618983f8741c5ef68d3a101e8a3b8cac60c905c15fc910840b94c00a0b9d0",
            "0aab4c900501b3e24d7cdf4663326a3a87df5e4843b2cbdb67cbf6e460fec350aa5371b1508f9f4528ecea23c436d94b5e8fcd4f681e30a6ac00a9704a188a03"
        }
    };

    [Theory]
    [MemberData(nameof(Rfc8032Vectors))]
    public void Verify_AcceptsRfc8032OfficialVectors(string publicKeyHex, string messageHex, string signatureHex)
    {
        Assert.True(PluginEd25519.Verify(
            Convert.FromHexString(signatureHex),
            Convert.FromHexString(messageHex),
            Convert.FromHexString(publicKeyHex)));
    }
}
