using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using DirectUpload.Contracts;
using DirectUpload.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DirectUpload.Server.Tests;

public class DirectUploadServiceTests
{
    private const long MiB = PartSizing.MiB;
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly IUploadLifecycleHandler _lifecycle = Substitute.For<IUploadLifecycleHandler>();
    private readonly DirectUploadOptions _options = new()
    {
        BucketName = "bucket",
        SigningKey = "a-signing-key-that-is-at-least-32-bytes-long",
        PartSize = 5 * MiB,
        MaxFileSize = 100 * MiB,
    };
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));

    public DirectUploadServiceTests()
    {
        _s3.Config.Returns(new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true });
        _s3.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new InitiateMultipartUploadResponse { UploadId = "upload-1" });
        _s3.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>())
            .Returns(ci => $"http://localhost:9000/bucket/key?partNumber={ci.Arg<GetPreSignedUrlRequest>().PartNumber}");
        _s3.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CompleteMultipartUploadResponse { ETag = "\"abc-3\"" });
    }

    private DirectUploadService CreateService() => new(
        _s3, Options.Create(_options), new DefaultUploadKeyPolicy(Options.Create(_options)), _lifecycle,
        new UploadTokenSigner(_options.SigningKey, _time), _time, NullLogger<DirectUploadService>.Instance);

    private static HttpContext User(string id) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test")),
    };

    private Task<StartUploadResponse> StartAsync(long size = 12 * MiB, string user = "alice") =>
        CreateService().StartAsync(new StartUploadRequest { FileName = "movie.mp4", ContentType = "video/mp4", Size = size }, User(user), default);

    private static CompletedPart[] Parts(int count) =>
        Enumerable.Range(1, count).Select(n => new CompletedPart { PartNumber = n, ETag = $"etag{n}" }).ToArray();

    [Fact]
    public async Task Start_plans_parts_and_creates_multipart_upload_under_policy_key()
    {
        var started = await StartAsync();

        Assert.Equal(5 * MiB, started.PartSize);
        Assert.Equal(3, started.PartCount);
        Assert.Matches("^uploads/[0-9a-f]{32}/movie.mp4$", started.Key);
        await _s3.Received(1).InitiateMultipartUploadAsync(
            Arg.Is<InitiateMultipartUploadRequest>(r => r.BucketName == "bucket" && r.Key == started.Key && r.ContentType == "video/mp4"),
            Arg.Any<CancellationToken>());
        await _lifecycle.Received(1).OnStartedAsync(Arg.Is<UploadLifecycleContext>(c => c.UserId == "alice" && c.Size == 12 * MiB), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_rejects_files_over_the_maximum()
    {
        var ex = await Assert.ThrowsAsync<UploadRequestException>(() => StartAsync(size: 100 * MiB + 1));
        Assert.Equal(413, ex.StatusCode);
        await _s3.DidNotReceiveWithAnyArgs().InitiateMultipartUploadAsync(default(InitiateMultipartUploadRequest)!, default);
    }

    [Fact]
    public async Task Start_rejects_disallowed_content_types()
    {
        _options.AllowedContentTypes.Add("audio/*");

        var ex = await Assert.ThrowsAsync<UploadRequestException>(() => StartAsync());
        Assert.Contains("video/mp4", ex.Message);
    }

    [Fact]
    public async Task Part_urls_are_presigned_for_put_with_upload_id_and_http_for_http_endpoints()
    {
        var started = await StartAsync();

        var response = await CreateService().GetPartUrlsAsync(new PartUrlsRequest { Token = started.Token, PartNumbers = [1, 3] }, User("alice"));

        Assert.Equal([1, 3], response.Urls.Select(u => u.PartNumber));
        await _s3.Received(2).GetPreSignedURLAsync(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.Verb == HttpVerb.PUT && r.UploadId == "upload-1" && r.Key == started.Key && r.Protocol == Protocol.HTTP &&
            r.Expires == _time.GetUtcNow().AddHours(1).UtcDateTime));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Part_urls_outside_the_planned_range_are_rejected(int partNumber)
    {
        var started = await StartAsync();

        await Assert.ThrowsAsync<UploadRequestException>(() =>
            CreateService().GetPartUrlsAsync(new PartUrlsRequest { Token = started.Token, PartNumbers = [partNumber] }, User("alice")));
    }

    [Fact]
    public async Task Another_user_cannot_continue_an_upload()
    {
        var started = await StartAsync(user: "alice");

        var ex = await Assert.ThrowsAsync<UploadRequestException>(() =>
            CreateService().AbortAsync(new AbortUploadRequest { Token = started.Token }, User("mallory"), default));
        Assert.Equal(403, ex.StatusCode);
        await _s3.DidNotReceiveWithAnyArgs().AbortMultipartUploadAsync(default(AbortMultipartUploadRequest)!, default);
    }

    [Fact]
    public async Task Complete_sends_sorted_parts_verifies_size_and_returns_result()
    {
        var started = await StartAsync();
        _s3.GetObjectMetadataAsync("bucket", started.Key, Arg.Any<CancellationToken>())
            .Returns(new GetObjectMetadataResponse { ContentLength = 12 * MiB });

        var result = await CreateService().CompleteAsync(
            new CompleteUploadRequest { Token = started.Token, Parts = Parts(3).Reverse().ToArray() }, User("alice"), default);

        Assert.Equal(started.Key, result.Key);
        Assert.Equal(12 * MiB, result.Size);
        Assert.Equal("abc-3", result.ETag);
        await _s3.Received(1).CompleteMultipartUploadAsync(
            Arg.Is<CompleteMultipartUploadRequest>(r => r.UploadId == "upload-1" && r.PartETags.Select(p => p.PartNumber ?? 0).SequenceEqual(new[] { 1, 2, 3 })),
            Arg.Any<CancellationToken>());
        await _lifecycle.Received(1).OnCompletedAsync(Arg.Is<UploadCompletedContext>(c => c.ETag == "abc-3"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Complete_deletes_the_object_when_its_size_differs_from_the_announced_size()
    {
        var started = await StartAsync();
        _s3.GetObjectMetadataAsync("bucket", started.Key, Arg.Any<CancellationToken>())
            .Returns(new GetObjectMetadataResponse { ContentLength = 13 * MiB });

        await Assert.ThrowsAsync<UploadRequestException>(() => CreateService().CompleteAsync(
            new CompleteUploadRequest { Token = started.Token, Parts = Parts(3) }, User("alice"), default));
        await _s3.Received(1).DeleteObjectAsync("bucket", started.Key, Arg.Any<CancellationToken>());
        await _lifecycle.DidNotReceiveWithAnyArgs().OnCompletedAsync(default!, default);
    }

    [Fact]
    public async Task Complete_with_missing_part_is_rejected_before_calling_storage()
    {
        var started = await StartAsync();

        var ex = await Assert.ThrowsAsync<UploadRequestException>(() => CreateService().CompleteAsync(
            new CompleteUploadRequest { Token = started.Token, Parts = Parts(2) }, User("alice"), default));
        Assert.Contains("Expected 3 parts", ex.Message);
        await _s3.DidNotReceiveWithAnyArgs().CompleteMultipartUploadAsync(default(CompleteMultipartUploadRequest)!, default);
    }

    [Fact]
    public void Missing_etag_points_to_the_cors_documentation()
    {
        var parts = Parts(2);
        parts[1].ETag = "";

        var ex = Assert.Throws<UploadRequestException>(() => DirectUploadService.ValidateParts(parts, 2));
        Assert.Contains("ExposeHeaders", ex.Message);
        Assert.Contains("README", ex.Message);
    }

    [Fact]
    public void Duplicate_part_numbers_are_rejected()
    {
        var parts = Parts(2);
        parts[1].PartNumber = 1;

        Assert.Throws<UploadRequestException>(() => DirectUploadService.ValidateParts(parts, 2));
    }

    [Fact]
    public async Task Storage_rejecting_a_part_becomes_a_client_error()
    {
        var started = await StartAsync();
        _s3.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Throws(new AmazonS3Exception("One or more parts could not be found.") { ErrorCode = "InvalidPart" });

        var ex = await Assert.ThrowsAsync<UploadRequestException>(() => CreateService().CompleteAsync(
            new CompleteUploadRequest { Token = started.Token, Parts = Parts(3) }, User("alice"), default));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("InvalidPart", ex.Message);
    }

    [Fact]
    public async Task Abort_is_idempotent_when_the_upload_is_already_gone()
    {
        var started = await StartAsync();
        _s3.AbortMultipartUploadAsync(Arg.Any<AbortMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Throws(new AmazonS3Exception("gone") { ErrorCode = "NoSuchUpload" });

        await CreateService().AbortAsync(new AbortUploadRequest { Token = started.Token }, User("alice"), default);

        await _lifecycle.Received(1).OnAbortedAsync(Arg.Any<UploadLifecycleContext>(), Arg.Any<CancellationToken>());
    }
}

public class DefaultUploadKeyPolicyTests
{
    [Theory]
    [InlineData("movie.mp4", "movie.mp4")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\me\My Song (final).wav", "My_Song__final_.wav")]
    [InlineData("..", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("naïve café.txt", "na_ve_caf_.txt")]
    public void File_names_are_sanitized_to_safe_key_segments(string? input, string expected)
    {
        Assert.Equal(expected, DefaultUploadKeyPolicy.SanitizeFileName(input));
    }

    [Fact]
    public void Long_file_names_keep_their_extension()
    {
        var name = DefaultUploadKeyPolicy.SanitizeFileName(new string('a', 300) + ".mp4");

        Assert.Equal(200, name.Length);
        Assert.EndsWith(".mp4", name);
    }
}
