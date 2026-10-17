namespace DirectUpload.Blazor;

/// <summary>A file selected in a file input. Only metadata; the bytes stay in the browser.</summary>
public sealed record SelectedFile(string Name, long Size, string ContentType);

/// <summary>Bytes sent so far.</summary>
public readonly record struct UploadProgress(long BytesUploaded, long TotalBytes)
{
    /// <summary>Progress between 0 and 1 (1 for an empty file).</summary>
    public double Fraction => TotalBytes == 0 ? 1 : (double)BytesUploaded / TotalBytes;
}

/// <summary>The stored object.</summary>
public sealed record UploadResult(string Key, long Size, string ETag);

/// <summary>Lifecycle state of an <see cref="UploadHandle"/>.</summary>
public enum UploadStatus
{
    /// <summary>Uploading.</summary>
    Running,
    /// <summary>Stored successfully.</summary>
    Completed,
    /// <summary>Failed; the multipart upload was aborted.</summary>
    Failed,
    /// <summary>Cancelled by the app; the multipart upload was aborted.</summary>
    Cancelled,
}

/// <summary>An upload failed. The message says why (part failure, CORS, server rejection).</summary>
public sealed class DirectUploadException(string message, string? cleanupError = null)
    : Exception(cleanupError is null ? message : $"{message} (cleanup also failed: {cleanupError})")
{
    /// <summary>
    /// Set when aborting the multipart upload on the server failed too. The incomplete upload is then
    /// left to the bucket lifecycle rule.
    /// </summary>
    public string? CleanupError { get; } = cleanupError;
}
