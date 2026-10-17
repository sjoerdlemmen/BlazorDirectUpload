using Amazon.S3;
using Amazon.S3.Model;
using DirectUpload.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DirectUpload.Server;

/// <summary>A request the client got wrong (or is not allowed to make). Mapped to a problem response.</summary>
internal sealed class UploadRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The S3 orchestration behind the four endpoints. Stateless: everything needed to continue an
/// upload travels in the signed <see cref="UploadToken"/>.
/// </summary>
internal sealed class DirectUploadService(
    IAmazonS3 s3,
    IOptions<DirectUploadOptions> options,
    IUploadKeyPolicy keyPolicy,
    IUploadLifecycleHandler lifecycle,
    UploadTokenSigner signer,
    TimeProvider time,
    ILogger<DirectUploadService> logger)
{
    public const int MaxPartUrlsPerRequest = 1000;

    private DirectUploadOptions Options => options.Value;

    public async Task<StartUploadResponse> StartAsync(StartUploadRequest request, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
            throw new UploadRequestException(400, "File name is required.");
        if (request.Size < 0)
            throw new UploadRequestException(400, "File size cannot be negative.");
        var maxSize = Math.Min(Options.MaxFileSize, PartSizing.MaxObjectSize);
        if (request.Size > maxSize)
            throw new UploadRequestException(413, $"File is {request.Size} bytes; the maximum is {maxSize} bytes.");

        var userId = Options.UserIdResolver(http);
        var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType;
        var decision = await keyPolicy.DecideAsync(new UploadRequestContext(request.FileName, contentType, request.Size, userId, http), ct);
        if (decision.Key is null)
            throw new UploadRequestException(400, decision.RejectionReason ?? "Upload rejected.");
        contentType = decision.ContentType ?? contentType;

        var plan = PartSizing.Plan(request.Size, Options.PartSize);
        var initiate = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = Options.BucketName,
            Key = decision.Key,
            ContentType = contentType,
        }, ct);

        var token = new UploadToken(
            decision.Key, initiate.UploadId, userId, request.FileName, contentType, request.Size,
            plan.PartSize, plan.PartCount, time.GetUtcNow().Add(Options.TokenLifetime).ToUnixTimeSeconds());

        logger.LogInformation("Upload started: {Key} ({Parts} parts of {PartSize} bytes, {Size} bytes total)",
            token.Key, plan.PartCount, plan.PartSize, request.Size);
        await lifecycle.OnStartedAsync(ToContext(token, http), ct);

        return new StartUploadResponse
        {
            Token = signer.Sign(token),
            Key = token.Key,
            PartSize = plan.PartSize,
            PartCount = plan.PartCount,
        };
    }

    public async Task<PartUrlsResponse> GetPartUrlsAsync(PartUrlsRequest request, HttpContext http)
    {
        var token = Validate(request.Token, http);
        if (request.PartNumbers.Length is 0 or > MaxPartUrlsPerRequest)
            throw new UploadRequestException(400, $"Request between 1 and {MaxPartUrlsPerRequest} part numbers.");

        var urls = new List<PartUrl>(request.PartNumbers.Length);
        foreach (var partNumber in request.PartNumbers.Distinct())
        {
            if (partNumber < 1 || partNumber > token.PartCount)
                throw new UploadRequestException(400, $"Part number {partNumber} is outside 1..{token.PartCount}.");
            urls.Add(new PartUrl { PartNumber = partNumber, Url = await PresignPartAsync(token, partNumber) });
        }
        return new PartUrlsResponse { Urls = [.. urls] };
    }

    public async Task<CompleteUploadResponse> CompleteAsync(CompleteUploadRequest request, HttpContext http, CancellationToken ct)
    {
        var token = Validate(request.Token, http);
        ValidateParts(request.Parts, token.PartCount);

        CompleteMultipartUploadResponse completed;
        try
        {
            completed = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = Options.BucketName,
                Key = token.Key,
                UploadId = token.UploadId,
                PartETags = request.Parts.OrderBy(p => p.PartNumber).Select(p => new PartETag(p.PartNumber, p.ETag)).ToList(),
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "InvalidPart" or "InvalidPartOrder" or "EntityTooSmall" or "NoSuchUpload")
        {
            throw new UploadRequestException(400, $"Storage rejected the upload ({ex.ErrorCode}): {ex.Message}");
        }

        // The token fixes the size the policy approved; a client that sent different bytes loses the object.
        var metadata = await s3.GetObjectMetadataAsync(Options.BucketName, token.Key, ct);
        var actualSize = metadata.ContentLength;
        if (actualSize != token.Size)
        {
            await s3.DeleteObjectAsync(Options.BucketName, token.Key, ct);
            throw new UploadRequestException(400,
                $"Uploaded object is {actualSize} bytes but {token.Size} bytes were announced; the object was deleted.");
        }

        var eTag = (completed.ETag ?? "").Trim('"');
        logger.LogInformation("Upload completed: {Key} ({Size} bytes)", token.Key, token.Size);
        await lifecycle.OnCompletedAsync(new UploadCompletedContext(
            token.Key, token.UploadId, token.UserId, token.FileName, token.ContentType, token.Size, eTag, http), ct);

        return new CompleteUploadResponse { Key = token.Key, Size = token.Size, ETag = eTag };
    }

    public async Task AbortAsync(AbortUploadRequest request, HttpContext http, CancellationToken ct)
    {
        var token = Validate(request.Token, http);
        try
        {
            await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = Options.BucketName,
                Key = token.Key,
                UploadId = token.UploadId,
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload")
        {
            // Already aborted or completed: aborting is idempotent.
        }
        logger.LogInformation("Upload aborted: {Key}", token.Key);
        await lifecycle.OnAbortedAsync(ToContext(token, http), ct);
    }

    internal static void ValidateParts(CompletedPart[] parts, int expectedCount)
    {
        if (parts.Length != expectedCount)
            throw new UploadRequestException(400, $"Expected {expectedCount} parts, got {parts.Length}.");

        var seen = new bool[expectedCount + 1];
        foreach (var part in parts)
        {
            if (part.PartNumber < 1 || part.PartNumber > expectedCount)
                throw new UploadRequestException(400, $"Part number {part.PartNumber} is outside 1..{expectedCount}.");
            if (seen[part.PartNumber])
                throw new UploadRequestException(400, $"Part number {part.PartNumber} appears more than once.");
            seen[part.PartNumber] = true;
            if (string.IsNullOrWhiteSpace(part.ETag))
                throw new UploadRequestException(400,
                    $"Part {part.PartNumber} has no ETag. The bucket CORS policy must expose the ETag header " +
                    "(ExposeHeaders: [\"ETag\"]); see the README section 'Bucket CORS'.");
        }
    }

    private UploadToken Validate(string token, HttpContext http)
    {
        var result = signer.Validate(token, Options.UserIdResolver(http), out var parsed);
        return result switch
        {
            TokenValidation.Valid => parsed!,
            TokenValidation.Expired => throw new UploadRequestException(400, "Upload token expired."),
            TokenValidation.WrongUser => throw new UploadRequestException(403, "Upload token belongs to another user."),
            _ => throw new UploadRequestException(400, "Invalid upload token."),
        };
    }

    private Task<string> PresignPartAsync(UploadToken token, int partNumber)
    {
        var serviceUrl = s3.Config.ServiceURL;
        return s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Options.BucketName,
            Key = token.Key,
            Verb = HttpVerb.PUT,
            UploadId = token.UploadId,
            PartNumber = partNumber,
            Expires = time.GetUtcNow().Add(Options.UrlExpiry).UtcDateTime,
            // Presigned URLs default to https; honour an explicit http endpoint (local MinIO-style servers).
            Protocol = serviceUrl is not null && serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? Protocol.HTTP
                : Protocol.HTTPS,
        });
    }

    private static UploadLifecycleContext ToContext(UploadToken token, HttpContext http) =>
        new(token.Key, token.UploadId, token.UserId, token.FileName, token.ContentType, token.Size, http);
}
