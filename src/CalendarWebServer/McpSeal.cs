using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CosmosCalendar.Web;

/// <summary>
/// What the OAuth of the MCP mode hands out, sealed with the secret of the site
/// server: registered clients, requests on their way through the consent page,
/// codes and tokens. The server has no database of its own and gets none for this:
/// whatever it gave out comes back to it readable only by it, and a thing that was
/// tampered with, or sealed for another purpose, simply does not open.
/// </summary>
public sealed class McpSeal
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _secret;

    public McpSeal(string secret)
    {
        _secret = Encoding.UTF8.GetBytes(secret);
    }

    public string Close<T>(string purpose, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        var sealedBytes = new byte[NonceBytes + plain.Length + TagBytes];
        var nonce = sealedBytes.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(KeyFor(purpose), TagBytes);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceBytes, plain.Length), sealedBytes.AsSpan(NonceBytes + plain.Length));
        return Base64Url.EncodeToString(sealedBytes);
    }

    /// <summary>The value, or default when this is not something sealed here for @p purpose.</summary>
    public T? Open<T>(string purpose, string? text) where T : class
    {
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            var sealedBytes = Base64Url.DecodeFromChars(text);
            if (sealedBytes.Length < NonceBytes + TagBytes)
                return null;
            var plain = new byte[sealedBytes.Length - NonceBytes - TagBytes];
            using var aes = new AesGcm(KeyFor(purpose), TagBytes);
            aes.Decrypt(sealedBytes.AsSpan(0, NonceBytes), sealedBytes.AsSpan(NonceBytes, plain.Length),
                sealedBytes.AsSpan(NonceBytes + plain.Length), plain);
            return JsonSerializer.Deserialize<T>(plain);
        }
        catch (Exception failure) when (failure is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    private byte[] KeyFor(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _secret, 32, info: Encoding.UTF8.GetBytes("cosmos-mcp/" + purpose));
}
