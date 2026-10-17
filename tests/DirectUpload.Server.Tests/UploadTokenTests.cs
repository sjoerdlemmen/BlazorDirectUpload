using DirectUpload.Server;

namespace DirectUpload.Server.Tests;

public class UploadTokenTests
{
    private const string Key = "a-signing-key-that-is-at-least-32-bytes-long";
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));

    private UploadToken NewToken(string user = "alice") => new(
        "uploads/x/file.bin", "upload-1", user, "file.bin", "application/octet-stream",
        12, 5, 1, _time.GetUtcNow().AddHours(1).ToUnixTimeSeconds());

    [Fact]
    public void Signed_token_round_trips()
    {
        var signer = new UploadTokenSigner(Key, _time);
        var token = NewToken();

        Assert.Equal(TokenValidation.Valid, signer.Validate(signer.Sign(token), "alice", out var parsed));
        Assert.Equal(token, parsed);
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var signer = new UploadTokenSigner(Key, _time);
        var signed = signer.Sign(NewToken());
        var otherPayload = signer.Sign(NewToken() with { Key = "uploads/someone-else/secret.bin" }).Split('.')[0];
        var forged = otherPayload + "." + signed.Split('.')[1];

        Assert.Equal(TokenValidation.BadSignature, signer.Validate(forged, "alice", out _));
    }

    [Fact]
    public void Token_signed_with_another_key_is_rejected()
    {
        var signed = new UploadTokenSigner(Key + "-other", _time).Sign(NewToken());

        Assert.Equal(TokenValidation.BadSignature, new UploadTokenSigner(Key, _time).Validate(signed, "alice", out _));
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var signer = new UploadTokenSigner(Key, _time);
        var signed = signer.Sign(NewToken());
        _time.Now = _time.Now.AddHours(1);

        Assert.Equal(TokenValidation.Expired, signer.Validate(signed, "alice", out _));
    }

    [Fact]
    public void Token_of_another_user_is_rejected()
    {
        var signer = new UploadTokenSigner(Key, _time);

        Assert.Equal(TokenValidation.WrongUser, signer.Validate(signer.Sign(NewToken("alice")), "mallory", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot")]
    [InlineData(".abc")]
    [InlineData("abc.")]
    [InlineData("!!!.???")]
    public void Malformed_tokens_are_rejected(string? value)
    {
        Assert.Equal(TokenValidation.Malformed, new UploadTokenSigner(Key, _time).Validate(value, "alice", out _));
    }

    [Fact]
    public void Short_signing_key_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new UploadTokenSigner("too-short", _time));
        Assert.Contains("32 bytes", ex.Message);
    }
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
