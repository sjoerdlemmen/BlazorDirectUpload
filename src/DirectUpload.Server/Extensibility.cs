using Microsoft.AspNetCore.Http;

namespace DirectUpload.Server;

/// <summary>
/// Decides the object key for a new upload and whether the upload is allowed at all.
/// The client never chooses the key. Register your own implementation to control prefixes,
/// per-user folders, file-type rules or quotas.
/// </summary>
public interface IUploadKeyPolicy
{
    /// <summary>Allow the upload with a key, or reject it with a reason shown to the client.</summary>
    ValueTask<UploadKeyDecision> DecideAsync(UploadRequestContext context, CancellationToken cancellationToken);
}

/// <summary>The upload the client asked to start.</summary>
/// <param name="FileName">File name as reported by the browser. Untrusted.</param>
/// <param name="ContentType">Content type as reported by the browser. Untrusted, may be empty.</param>
/// <param name="Size">Size in bytes as reported by the browser. Enforced at completion.</param>
/// <param name="UserId">Id the upload token will be bound to.</param>
/// <param name="HttpContext">The current request.</param>
public sealed record UploadRequestContext(string FileName, string ContentType, long Size, string UserId, HttpContext HttpContext);

/// <summary>Outcome of <see cref="IUploadKeyPolicy.DecideAsync"/>.</summary>
public sealed class UploadKeyDecision
{
    private UploadKeyDecision(string? key, string? contentType, string? rejectionReason)
    {
        Key = key;
        ContentType = contentType;
        RejectionReason = rejectionReason;
    }

    /// <summary>Object key to upload to; null when rejected.</summary>
    public string? Key { get; }

    /// <summary>Content type stored on the object; null keeps the client-reported type.</summary>
    public string? ContentType { get; }

    /// <summary>Reason the upload was rejected; null when allowed.</summary>
    public string? RejectionReason { get; }

    /// <summary>Allow the upload to <paramref name="key"/>.</summary>
    public static UploadKeyDecision Allow(string key, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new(key, contentType, null);
    }

    /// <summary>Reject the upload; <paramref name="reason"/> is returned to the client.</summary>
    public static UploadKeyDecision Reject(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(null, null, reason);
    }
}

/// <summary>
/// Hooks into the upload lifecycle, e.g. to write a database row. All methods are optional.
/// An exception thrown from a hook fails the corresponding request.
/// </summary>
public interface IUploadLifecycleHandler
{
    /// <summary>Called after the multipart upload was created in storage.</summary>
    Task OnStartedAsync(UploadLifecycleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Called after the object was assembled and its size verified.</summary>
    Task OnCompletedAsync(UploadCompletedContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Called after the multipart upload was aborted (cancel or failure).</summary>
    Task OnAbortedAsync(UploadLifecycleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>An upload in progress.</summary>
public record UploadLifecycleContext(
    string Key, string UploadId, string UserId, string FileName, string ContentType, long Size, HttpContext HttpContext);

/// <summary>A completed upload.</summary>
public sealed record UploadCompletedContext(
    string Key, string UploadId, string UserId, string FileName, string ContentType, long Size, string ETag, HttpContext HttpContext)
    : UploadLifecycleContext(Key, UploadId, UserId, FileName, ContentType, Size, HttpContext);

internal sealed class NullUploadLifecycleHandler : IUploadLifecycleHandler;

/// <summary>
/// Default policy: <c>{KeyPrefix}{guid}/{sanitized file name}</c>, enforcing
/// <see cref="DirectUploadOptions.AllowedContentTypes"/>.
/// </summary>
public sealed class DefaultUploadKeyPolicy(Microsoft.Extensions.Options.IOptions<DirectUploadOptions> options) : IUploadKeyPolicy
{
    /// <inheritdoc />
    public ValueTask<UploadKeyDecision> DecideAsync(UploadRequestContext context, CancellationToken cancellationToken)
    {
        var allowed = options.Value.AllowedContentTypes;
        if (allowed.Count > 0 && !allowed.Any(a => ContentTypeMatches(a, context.ContentType)))
            return ValueTask.FromResult(UploadKeyDecision.Reject($"Content type '{context.ContentType}' is not allowed."));

        var key = $"{options.Value.KeyPrefix}{Guid.NewGuid():N}/{SanitizeFileName(context.FileName)}";
        return ValueTask.FromResult(UploadKeyDecision.Allow(key));
    }

    /// <summary>
    /// Reduces a browser-supplied file name to a safe key segment: no path, only
    /// <c>[A-Za-z0-9._-]</c>, at most 200 characters, never empty or dot-only.
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = (fileName ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        var safe = new string(chars).Trim('.');
        if (safe.Length > 200)
            safe = safe[^200..];
        return safe.Length == 0 ? "file" : safe;
    }

    private static bool ContentTypeMatches(string pattern, string contentType) =>
        pattern.EndsWith("/*", StringComparison.Ordinal)
            ? contentType.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, contentType, StringComparison.OrdinalIgnoreCase);
}
