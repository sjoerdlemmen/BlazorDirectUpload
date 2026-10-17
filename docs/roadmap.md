# Roadmap

Deliberately out of scope for v0.1 (candidates for a later or Pro layer):

- **Resume after page reload**: persist upload token + completed parts in IndexedDB; the server lists
  already-stored parts with `ListParts` so only missing parts are re-sent.
- **Azure Blob Storage**: different model (SAS URL + Put Block / Put Block List); would be a separate
  storage backend behind the same client.
- **Drag-and-drop, thumbnails, ready-made UI themes**.
- **Per-part checksums** (`x-amz-checksum-*` headers signed into the presigned URL).

Smaller ideas:

- Pause/resume within a session.
- Overall upload speed and ETA in `UploadProgress`.
- Optional server-side `ListParts` check before `complete`.
