using System.Security.Cryptography;

namespace NetworkHidBridge.Core.Protocol;

public static class SessionAuthenticator
{
    public const int KeyLength = 32;
    public const int NonceLength = 32;
    public const int ProofLength = 32;

    public static byte[] DeriveServerProof(byte[] psk, byte[] clientNonce, byte[] serverNonce) =>
        Derive("server"u8, psk, clientNonce, serverNonce);

    public static byte[] DeriveClientProof(byte[] psk, byte[] clientNonce, byte[] serverNonce) =>
        Derive("client"u8, psk, clientNonce, serverNonce);

    public static byte[] DeriveSessionKey(byte[] psk, byte[] clientNonce, byte[] serverNonce) =>
        Derive("session"u8, psk, clientNonce, serverNonce);

    public static bool VerifyServerProof(byte[] proof, byte[] psk, byte[] clientNonce, byte[] serverNonce) =>
        Verify(proof, DeriveServerProof(psk, clientNonce, serverNonce));

    public static bool VerifyClientProof(byte[] proof, byte[] psk, byte[] clientNonce, byte[] serverNonce) =>
        Verify(proof, DeriveClientProof(psk, clientNonce, serverNonce));

    private static byte[] Derive(ReadOnlySpan<byte> label, byte[] psk, byte[] clientNonce, byte[] serverNonce)
    {
        ValidateLength(psk, KeyLength, nameof(psk));
        ValidateLength(clientNonce, NonceLength, nameof(clientNonce));
        ValidateLength(serverNonce, NonceLength, nameof(serverNonce));
        var transcript = new byte[label.Length + NonceLength + NonceLength];
        label.CopyTo(transcript);
        clientNonce.CopyTo(transcript, label.Length);
        serverNonce.CopyTo(transcript, label.Length + NonceLength);
        var result = HMACSHA256.HashData(psk, transcript);
        CryptographicOperations.ZeroMemory(transcript);
        return result;
    }

    private static bool Verify(byte[] proof, byte[] expected)
    {
        ArgumentNullException.ThrowIfNull(proof);
        try
        {
            return proof.Length == ProofLength && CryptographicOperations.FixedTimeEquals(proof, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static void ValidateLength(byte[] value, int expectedLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != expectedLength)
        {
            throw new ArgumentException($"{parameterName} must be exactly {expectedLength} bytes.", parameterName);
        }
    }
}
