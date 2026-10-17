# TASK_STATE — BlazorDirectUpload v0.1

**Goal:** see KICKOFF.md §1/§8. Build v0.1 ready for GitHub (github.com/sjoerdlemmen), not yet published.

## Plan / status
- [x] Solution skeleton, docker-compose (S3-compatible storage), CORS bootstrap in sample
- [x] Server: options, part sizing, HMAC upload token, key policy, lifecycle hooks, 4 endpoints + unit tests
- [x] JS module + DirectUploader + UploadHandle + DirectUploadInput
- [x] Sample app: /, /server, /wasm (Auto, Server, WebAssembly)
- [ ] Playwright E2E (happy path x3 modes, cancel, 403 refresh, permanent part failure, two concurrent)
- [ ] README, docs/design.md, docs/roadmap.md, LICENSE, CI workflow, dotnet pack
- [ ] Manual: 3 GB upload local; R2 (needs credentials from Sjoerd)

## Decisions
- MinIO official images are gone (Docker Hub + quay.io refuse anonymous pulls, MinIO CE retired). Using RustFS
  (S3-compatible, MinIO-style) in docker-compose. Code is provider-agnostic.
- JS calls the upload endpoints itself (fetch, same-origin cookies) instead of routing through .NET. Works the
  same in WASM/Server/Auto, no HttpClient/auth forwarding on Blazor Server, 403 refresh needs no .NET round trip.
- Part URLs are fetched lazily in batches (default 50), not all at start: keeps 10,000-part uploads cheap and
  URLs fresh.
- XHR instead of fetch for part PUTs: byte-level upload progress (fetch has none).
- Completion is a .NET callback, not an awaited JS promise: Blazor Server's JS interop call timeout (1 min)
  would kill long uploads.
- complete HEADs the object and deletes it if the size differs from the signed size.
- 0-byte files: one empty part. Verified against RustFS.
- Default part size 16 MiB.

## Blockers
- R2 verification needs credentials (Sjoerd).
- GitHub repo creation/push and NuGet publish need explicit approval.
