using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DirectUpload.Server;

/// <summary>
/// Everything the server needs to continue an upload, carried by the client and protected by an
/// HMAC-SHA256 signature. This keeps the server stateless while making key, upload id, owner and
/// size tamper-proof.
/// </summary>
internal sealed record UploadToken(
    [property: JsonPropertyName("k")] string Key,
    [property: JsonPropertyName("u")] string UploadId,
    [property: JsonPropertyName("sub")] string UserId,
    [property: JsonPropertyName("n")] string FileName,
    [property: JsonPropertyName("ct")] string ContentType,
    [property: JsonPropertyName("s")] long Size,
    [property: JsonPropertyName("ps")] long PartSize,
    [property: JsonPropertyName("pc")] int PartCount,
    [property: JsonPropertyName("exp")] long ExpiresAtUnix);

internal enum TokenValidation { Valid, Malformed, BadSignature, Expired, WrongUser }

internal sealed class UploadTokenSigner
{
    private readonly byte[] _key;
    private readonly TimeProvider _time;

    public UploadTokenSigner(string signingKey, TimeProvider time)
    {
        _key = Encoding.UTF8.GetBytes(signingKey ?? "");
        if (_key.Length < 32)
            throw new InvalidOperationException(
                "DirectUploadOptions.SigningKey must be at least 32 bytes. Generate one with e.g. `openssl rand -base64 48`.");
        _time = time;
    }

    public string Sign(UploadToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(token);
        return Base64Url(payload) + "." + Base64Url(HMACSHA256.HashData(_key, payload));
    }

    public TokenValidation Validate(string? value, string userId, out UploadToken? token)
    {
        token = null;
        var dot = value?.IndexOf('.') ?? -1;
        if (value is null || dot <= 0 || dot == value.Length - 1)
            return TokenValidation.Malformed;

        byte[] payload, signature;
        try
        {
            payload = FromBase64Url(value[..dot]);
            signature = FromBase64Url(value[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return TokenValidation.Malformed;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(_key, payload)))
            return TokenValidation.BadSignature;

        UploadToken? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<UploadToken>(payload);
        }
        catch (JsonException)
        {
            return TokenValidation.Malformed;
        }
        if (parsed is null)
            return TokenValidation.Malformed;
        if (_time.GetUtcNow().ToUnixTimeSeconds() >= parsed.ExpiresAtUnix)
            return TokenValidation.Expired;
        if (!string.Equals(parsed.UserId, userId, StringComparison.Ordinal))
            return TokenValidation.WrongUser;

        token = parsed;
        return TokenValidation.Valid;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
    }
}
