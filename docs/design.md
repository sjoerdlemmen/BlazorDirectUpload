# Design — BlazorDirectUpload v0.1

## Goal

Upload files of tens of GB from Blazor (WebAssembly, Server, Auto) directly to S3-compatible storage
via presigned multipart URLs. Bytes never touch the .NET server or WASM memory. Cancel cleans up,
expired URLs refresh transparently, and a permanently failing part fails the upload loudly.

## Components

```
DirectUpload.Server   (net8.0)  DirectUploadService  ── IAmazonS3 (app-registered)
                                 UploadTokenSigner (HMAC-SHA256)
                                 PartSizing
                                 IUploadKeyPolicy / IUploadLifecycleHandler (extension points)
                                 MapDirectUpload: POST start | part-urls | complete | abort
DirectUpload.Blazor   (net8.0)  wwwroot/directUpload.js  (slicing, XHR PUT, retry, abort, progress)
                                 DirectUploader → UploadHandle (Progress, Completion, CancelAsync)
                                 <DirectUploadInput>
src/Shared/Contracts.cs          wire DTOs, compiled into the server as internal types
```

## Verified APIs (AWSSDK.S3 4.0.104.4, ASP.NET Core 8 / Blazor)

All compiled against and exercised end-to-end against an S3-compatible server:

| Call | Notes |
|---|---|
| `InitiateMultipartUploadAsync(InitiateMultipartUploadRequest)` | `UploadId` from response |
| `GetPreSignedURLAsync(GetPreSignedUrlRequest { Verb = PUT, UploadId, PartNumber, Expires, Protocol })` | `Protocol` defaults to HTTPS; set HTTP for an `http://` ServiceURL |
| `CompleteMultipartUploadAsync(... PartETags = List<PartETag>)` | `PartETag.PartNumber` is `int?` in v4 |
| `GetObjectMetadataAsync(bucket, key)` | size verification after completion |
| `AbortMultipartUploadAsync(AbortMultipartUploadRequest)` | `NoSuchUpload` treated as already aborted |
| `PutCORSConfigurationAsync` | v4 name for PutBucketCors (sample bootstrap) |
| `IJSRuntime.InvokeAsync<IJSObjectReference>("import", ...)` | JS isolation module from `_content/DirectUpload.Blazor/` |
| `ElementReference` passed to JS | arrives as the DOM element; `input.files[i]` read in JS |
| `DotNetObjectReference<UploadHandle>` + `[JSInvokable]` | progress / completed / failed / cancelled callbacks |

## Key decisions

1. **JS calls the upload endpoints itself** (fetch, `credentials: "same-origin"`), rather than .NET
   via `HttpClient`. Identical in all render modes; on Blazor Server the C# code runs on the server
   and would otherwise need its own base address and auth forwarding. 403 refresh needs no .NET
   round trip. Bearer-token apps pass headers through `DirectUploadClientOptions.Headers`.
2. **Completion is a callback, not an awaited JS promise.** Blazor Server's JS interop call timeout
   (1 minute by default) would cancel a long `InvokeAsync`. `start` returns at once; JS reports
   `OnCompleted` / `OnFailed` / `OnCancelled`, which drive a `TaskCompletionSource`.
3. **Upload state per id** (`Map<id, state>` in JS): own `AbortController`, own XHR set, own
   cancelled/fatal flags. Cancelling or failing one upload never touches another.
4. **Part URLs are fetched lazily in batches** (default 50) instead of all at start. A 50 GB file at
   10,000 parts would otherwise mean ~6 MB of URLs, many expired before use.
5. **XHR for part PUTs**: `fetch` has no upload progress; XHR gives byte-level progress and the
   `ETag` via `getResponseHeader`.
6. **Stateless, tamper-proof token** instead of server state or prefix checks:
   `base64url(json{k,u,sub,n,ct,s,ps,pc,exp}) + "." + base64url(HMACSHA256(key, json))`, compared
   with `CryptographicOperations.FixedTimeEquals`.
7. **Size enforcement at completion**: part count must equal the signed plan and the object size
   must equal the signed size (object deleted otherwise).
8. **Local storage: RustFS**, because the MinIO community images are no longer pullable. The code
   only speaks the S3 API.

## Retry and failure semantics (JS)

Per part, up to `MaxAttempts` (default 5):

| Outcome | Action |
|---|---|
| 2xx with ETag | done |
| 2xx without readable ETag | fatal immediately: CORS `ExposeHeaders: ["ETag"]` message |
| 403 | drop cached URL, fetch a fresh one via `part-urls`, retry immediately |
| network error, 408, 429, 5xx | exponential backoff (0.5 s, 1 s, 2 s, ... max 8 s), retry |
| other 4xx | fatal immediately |
| attempts exhausted | **throw** (never fall out of the loop silently) |

A fatal part error marks the upload failed, aborts all sibling XHRs (`AbortController` + `xhr.abort()`),
waits for the workers to unwind, calls `abort` on the server and reports `OnFailed`. As a final guard
`complete` is never called unless exactly `partCount` parts have an ETag.

## Part sizing

`PartSizing.Plan(size, preferred)`:
- `partSize = clamp(preferred, 5 MiB, 5 GiB)`
- if `ceil(size / partSize) > 10,000`: `partSize = ceil(ceil(size / 10,000) / 1 MiB) * 1 MiB`
- `partCount = size == 0 ? 1 : ceil(size / partSize)`
- sizes above 5 TiB are rejected

### Trace: 12 MiB file, part size 5 MiB

1. `start {size: 12582912}` → plan `partSize = 5242880`, `partCount = ceil(12/5) = 3`;
   `CreateMultipartUpload` → `uploadId`; token signed with `s=12582912, ps=5242880, pc=3`.
2. JS: 3 workers would be allowed (concurrency 4, capped at 3 parts). URL cache requests
   `part-urls [1,2,3]` once (batch 50, capped at partCount).
3. Slices: part 1 `[0, 5 MiB)`, part 2 `[5 MiB, 10 MiB)`, part 3 `[10 MiB, 12 MiB)` = 2 MiB
   (allowed: only the last part may be < 5 MiB).
4. Each PUT returns an ETag; progress = finished bytes + in-flight `loaded`, reported at most every 250 ms.
5. `complete {parts: [{1,e1},{2,e2},{3,e3}]}` → count 3 == pc → `CompleteMultipartUpload` → HEAD size
   12582912 == s → `{key, size, eTag}`.

### Edge cases

| Case | Plan | Behaviour (verified) |
|---|---|---|
| 0 bytes | 1 part of 0 bytes | one empty PUT, completes (unit + E2E) |
| exactly one part (≤ part size) | 1 part, last = size | unit |
| exact multiple (15 MiB / 5 MiB) | 3 parts, last = 5 MiB | unit |
| > 10,000 × part size (e.g. 100 GiB) | part size grows in whole MiB | unit, up to 5 TiB |
| 3 GiB (5 MiB parts, 615 parts) | | E2E (opt-in large-file test) |

## Render modes

The JS module and the callbacks are the same in every mode; only the .NET side's location differs.
Blazor Server receives at most 4 progress callbacks per second per upload over SignalR. E2E tests run
the happy path in Auto, Server and WebAssembly, and concurrent-cancel in Server and WebAssembly.
