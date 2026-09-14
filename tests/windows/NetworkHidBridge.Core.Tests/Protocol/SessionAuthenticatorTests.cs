using System.Security.Cryptography;
using NetworkHidBridge.Core.Protocol;

namespace NetworkHidBridge.Core.Tests.Protocol;

public sealed class SessionAuthenticatorTests
{
    private static readonly byte[] Psk = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] ClientNonce = Enumerable.Range(0xa0, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] ServerNonce = Enumerable.Range(0xc0, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void DerivationsMatchFrozenVectors()
    {
        Assert.Equal("fab58c4e791978698600671a856766a87bfc6e92817773a11f8b339626370707",
            Convert.ToHexString(SessionAuthenticator.DeriveServerProof(Psk, ClientNonce, ServerNonce)).ToLowerInvariant());
        Assert.Equal("1cc0f445f69752c72559bce07d9cd441d3bafd7f9606a34c16d29e4f79310d61",
            Convert.ToHexString(SessionAuthenticator.DeriveClientProof(Psk, ClientNonce, ServerNonce)).ToLowerInvariant());
        Assert.Equal("ddfd4bcf514438966c64721355bdd8b01a8a808927214daa4e3b3219b4e5d951",
            Convert.ToHexString(SessionAuthenticator.DeriveSessionKey(Psk, ClientNonce, ServerNonce)).ToLowerInvariant());
    }

    [Fact]
    public void DerivationsUseExactLabelsAndNonceOrder()
    {
        var expected = HMACSHA256.HashData(Psk, "server"u8.ToArray().Concat(ClientNonce).Concat(ServerNonce).ToArray());
        var reversed = HMACSHA256.HashData(Psk, "server"u8.ToArray().Concat(ServerNonce).Concat(ClientNonce).ToArray());
        Assert.Equal(expected, SessionAuthenticator.DeriveServerProof(Psk, ClientNonce, ServerNonce));
        Assert.NotEqual(reversed, expected);
        Assert.NotEqual(SessionAuthenticator.DeriveClientProof(Psk, ClientNonce, ServerNonce), expected);
        Assert.NotEqual(SessionAuthenticator.DeriveSessionKey(Psk, ClientNonce, ServerNonce), expected);
    }

    [Theory]
    [InlineData(31, 32, 32)]
    [InlineData(33, 32, 32)]
    [InlineData(32, 31, 32)]
    [InlineData(32, 32, 33)]
    public void DerivationRejectsInvalidInputLengths(int pskLength, int clientLength, int serverLength)
    {
        Assert.Throws<ArgumentException>(() => SessionAuthenticator.DeriveServerProof(
            new byte[pskLength], new byte[clientLength], new byte[serverLength]));
    }

    [Fact]
    public void ProofValidationAcceptsValidAndRejectsWrongProof()
    {
        var serverProof = SessionAuthenticator.DeriveServerProof(Psk, ClientNonce, ServerNonce);
        var clientProof = SessionAuthenticator.DeriveClientProof(Psk, ClientNonce, ServerNonce);
        Assert.True(SessionAuthenticator.VerifyServerProof(serverProof, Psk, ClientNonce, ServerNonce));
        Assert.True(SessionAuthenticator.VerifyClientProof(clientProof, Psk, ClientNonce, ServerNonce));
        clientProof[0] ^= 1;
        Assert.False(SessionAuthenticator.VerifyClientProof(clientProof, Psk, ClientNonce, ServerNonce));
        Assert.False(SessionAuthenticator.VerifyClientProof(new byte[31], Psk, ClientNonce, ServerNonce));
    }
}
