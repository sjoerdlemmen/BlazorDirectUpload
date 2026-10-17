# BlazorDirectUpload

Upload large files (up to tens of GB) from a Blazor app **straight from the browser to S3-compatible
object storage** (AWS S3, Cloudflare R2, MinIO-style servers, Backblaze B2, Wasabi) using presigned
multipart URLs. The bytes never pass through your .NET server and never enter WebAssembly memory.

- Works in Blazor **WebAssembly, Server and Auto** render modes
- Parallel part uploads with retry and exponential backoff
- Expired presigned URLs (HTTP 403) are refreshed transparently
- A part that keeps failing fails the whole upload **loudly** and cleans up the multipart upload
- Cancel aborts the multipart upload on the server; each upload has its own handle and state
- Stateless, tamper-proof server: an HMAC-signed upload token binds key, upload id, user and size
- No UI opinion: a bare `<input type="file">` you style yourself

| Package | Use in |
|---|---|
| `DirectUpload.Server` | ASP.NET Core host: maps the `start` / `part-urls` / `complete` / `abort` endpoints |
| `DirectUpload.Blazor` | Blazor components (server and WebAssembly project): JS module, `DirectUploader`, `<DirectUploadInput>` |

## How it works

```
Browser (JS module)                    Your server (DirectUpload.Server)        Object storage
───────────────────                    ─────────────────────────────────        ──────────────
POST /uploads/start {name,type,size} ─▶ policy picks key, CreateMultipartUpload ─▶
                                     ◀─ {token, key, partSize, partCount}
POST /uploads/part-urls {token,[1..50]}▶ presign UploadPart URLs (no storage call)
                                     ◀─ [{partNumber, url}]
PUT part 1..n (4 in parallel) ─────────────────────────────────────────────────▶ stores part
                                     ◀───────────────────────────────────────── ETag
POST /uploads/complete {token,parts} ─▶ CompleteMultipartUpload, verify size ──▶
                                     ◀─ {key, size, eTag}
```

The `File` stays in JavaScript (`Blob.slice` per part). .NET only receives progress numbers and the
final result, throttled to 4 callbacks per second so Blazor Server's SignalR connection stays quiet.

## Quickstart

### 1. Server (the ASP.NET Core host)

```bash
dotnet add package DirectUpload.Server
```

```csharp
using Amazon.S3;

// Any IAmazonS3 works. Example for Cloudflare R2:
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    builder.Configuration["R2:AccessKey"], builder.Configuration["R2:SecretKey"],
    new AmazonS3Config
    {
        ServiceURL = "https://<account-id>.r2.cloudflarestorage.com",
        ForcePathStyle = true,
        AuthenticationRegion = "auto",
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    }));

builder.Services.AddDirectUpload(o =>
{
    o.BucketName = "my-bucket";
    o.SigningKey = builder.Configuration["DirectUpload:SigningKey"]!; // >= 32 bytes, keep secret
    o.MaxFileSize = 20L * 1024 * 1024 * 1024;                         // 20 GiB
});
builder.Services.AddDirectUploadClient(); // for prerendering and Server interactivity

// ...
app.MapDirectUpload("/uploads").RequireAuthorization();
```

### 2. Blazor (in the WebAssembly client project too, for WASM/Auto)

```bash
dotnet add package DirectUpload.Blazor
```

```csharp
builder.Services.AddDirectUploadClient();
```

```razor
@using DirectUpload.Blazor

<DirectUploadInput AutoStart="true" accept="video/*" OnUploadStarted="Track" />

@if (_upload is not null)
{
    <progress max="1" value="@_upload.Progress.Fraction"></progress>
    <button @onclick="_upload.CancelAsync">Cancel</button>
    <p>@_message</p>
}

@code {
    private UploadHandle? _upload;
    private string? _message;

    private async Task Track(UploadHandle upload)
    {
        _upload = upload;
        upload.ProgressChanged += _ => InvokeAsync(StateHasChanged);
        try
        {
            var result = await upload.Completion;  // UploadResult(Key, Size, ETag)
            _message = $"Stored as {result.Key}";
        }
        catch (DirectUploadException ex) { _message = ex.Message; }      // failed, cleaned up
        catch (OperationCanceledException) { _message = "Cancelled"; }    // cancelled, cleaned up
    }
}
```

Prefer code over the component? Inject `DirectUploader` and call
`StartAsync(elementReference, fileIndex, options)` on any `<input type="file">`.

Do **not** use Blazor's `InputFile` (or suite upload components) for this: they marshal bytes into .NET.

## Bucket CORS

Browsers PUT directly to the bucket, so the bucket needs a CORS rule that allows `PUT` from your
origin **and exposes the `ETag` header**. Without `ExposeHeaders: ["ETag"]` the browser cannot read
part ETags and the upload fails with a message pointing here.

**AWS S3** (`aws s3api put-bucket-cors --bucket my-bucket --cors-configuration file://cors.json`):

```json
{
  "CORSRules": [
    {
      "AllowedOrigins": ["https://app.example.com", "https://localhost:7171"],
      "AllowedMethods": ["PUT"],
      "AllowedHeaders": ["*"],
      "ExposeHeaders": ["ETag"],
      "MaxAgeSeconds": 3600
    }
  ]
}
```

**Cloudflare R2** (dashboard → bucket → Settings → CORS policy):

```json
[
  {
    "AllowedOrigins": ["https://app.example.com", "https://localhost:7171"],
    "AllowedMethods": ["PUT"],
    "AllowedHeaders": ["*"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3600
  }
]
```

**MinIO-style / other S3-compatible servers:** apply the AWS rule above with `PutBucketCors`
(AWSSDK: `PutCORSConfigurationAsync`). The sample app does this on startup in Development.

Remember the exact origins you develop on (scheme, host **and** port).

## Bucket lifecycle: abort incomplete uploads

An upload that is never completed or aborted (closed tab, crashed browser, lost network) leaves its
parts in the bucket, where they are billed but invisible in normal listings. Add a lifecycle rule:

**AWS S3** (`aws s3api put-bucket-lifecycle-configuration --bucket my-bucket --lifecycle-configuration file://lifecycle.json`):

```json
{
  "Rules": [
    {
      "ID": "abort-incomplete-multipart-uploads",
      "Status": "Enabled",
      "Filter": {},
      "AbortIncompleteMultipartUpload": { "DaysAfterInitiation": 3 }
    }
  ]
}
```

**Cloudflare R2:** bucket → Settings → Object lifecycle rules → add a rule that aborts incomplete
multipart uploads after N days (check whether your bucket already has the default 7-day rule).

**Other servers:** use their equivalent of `AbortIncompleteMultipartUpload`.

## Security model

- **The client never chooses the object key.** `IUploadKeyPolicy` decides it (default:
  `{KeyPrefix}{guid}/{sanitized-file-name}`) and can reject uploads by type, size or user.
- **Stateless but tamper-proof.** `start` returns a token containing key, upload id, user id, file
  name, content type, size, part plan and expiry, signed with HMAC-SHA256 (`SigningKey`). Follow-up
  calls send only the token; a modified token, an expired token or a token used by another user is
  rejected.
- **Size is enforced.** `MaxFileSize` is checked at start; at completion the object size is compared
  with the signed size and the object is deleted on mismatch.
- **Presigned URLs are narrow.** Each URL permits one `PUT` of one part of one upload, for
  `UrlExpiry` (default 1 hour). URLs are requested in batches as the upload progresses.
- **Authorization is yours.** Chain `.RequireAuthorization(...)` on `MapDirectUpload`. Endpoints take
  JSON bodies only, so cross-site form posts cannot reach them; cookies are sent `same-origin`.
  Bearer-token apps pass headers via `DirectUploadClientOptions.Headers`.
- `SigningKey` must be at least 32 bytes. Rotating it invalidates uploads in flight.

## Extension points

```csharp
public sealed class PerUserKeys : IUploadKeyPolicy
{
    public ValueTask<UploadKeyDecision> DecideAsync(UploadRequestContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(ctx.UserId == ""
            ? UploadKeyDecision.Reject("Sign in to upload.")
            : UploadKeyDecision.Allow($"users/{ctx.UserId}/{Guid.NewGuid():N}/{DefaultUploadKeyPolicy.SanitizeFileName(ctx.FileName)}"));
}

public sealed class SaveToDatabase(MyDbContext db) : IUploadLifecycleHandler
{
    public async Task OnCompletedAsync(UploadCompletedContext ctx, CancellationToken ct)
    {
        db.Files.Add(new StoredFile { Key = ctx.Key, Size = ctx.Size, Owner = ctx.UserId });
        await db.SaveChangesAsync(ct);
    }
}

builder.Services.AddSingleton<IUploadKeyPolicy, PerUserKeys>();
builder.Services.AddScoped<IUploadLifecycleHandler, SaveToDatabase>();
builder.Services.AddDirectUpload(o => { /* ... */ });
```

`OnStartedAsync` and `OnAbortedAsync` are available too; all hooks are optional.

## Options

| Server (`DirectUploadOptions`) | Default | |
|---|---|---|
| `BucketName` | (required) | |
| `SigningKey` | (required) | at least 32 bytes |
| `PartSize` | 16 MiB | raised to the 5 MiB S3 minimum, and grown automatically so a file never needs more than 10,000 parts |
| `MaxFileSize` | 50 GiB | hard ceiling 5 TiB (S3 limit) |
| `UrlExpiry` | 1 hour | per presigned part URL; expired URLs are refreshed |
| `TokenLifetime` | 24 hours | longest an upload may take |
| `KeyPrefix` | `uploads/` | default key policy |
| `AllowedContentTypes` | any | default key policy; supports `video/*` |
| `UserIdResolver` | NameIdentifier claim → identity name → `""` | the id tokens are bound to |

| Client (`DirectUploadClientOptions`) | Default | |
|---|---|---|
| `Endpoint` | `uploads` | relative to the document base URI |
| `Concurrency` | 4 | parallel part uploads |
| `MaxAttempts` | 5 | per part; then the upload fails and is aborted |
| `ProgressIntervalMs` | 250 | progress callbacks into .NET at most 4×/s |
| `UrlBatchSize` | 50 | part URLs per `part-urls` request |
| `Headers` | none | extra headers for the endpoint calls (not sent to storage) |

## Running the sample and the tests

```bash
docker compose up -d                       # S3-compatible storage on :9000 (RustFS, see below)
dotnet run --project samples/SampleApp/SampleApp
# open http://localhost:5212  (Auto), /server, /wasm

dotnet test tests/DirectUpload.Server.Tests
pwsh tests/DirectUpload.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test tests/DirectUpload.E2E        # Playwright: all render modes, cancel, 403 refresh, failures
DIRECTUPLOAD_LARGE_FILE_GB=3 dotnet test tests/DirectUpload.E2E --filter Large_file
```

The official MinIO community images are no longer published, so `docker-compose.yml` uses
[RustFS](https://github.com/rustfs/rustfs), an S3-compatible server with the same API. The library
itself is provider-agnostic.

## Releasing

Pushing a tag `vX.Y.Z` runs `.github/workflows/release.yml`: full build and tests, then
`dotnet pack` with the tag as version and a push to nuget.org via
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (no stored API key).

## Limitations (v0.1)

See [docs/roadmap.md](docs/roadmap.md): no resume after page reload, no Azure Blob Storage, no
drag-and-drop or ready-made UI, no per-part checksums.

## License

MIT, see [LICENSE](LICENSE).
