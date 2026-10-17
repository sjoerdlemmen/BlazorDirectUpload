using Microsoft.JSInterop;

namespace DirectUpload.Blazor;

/// <summary>
/// One running upload. Await <see cref="Completion"/> for the result, subscribe to
/// <see cref="ProgressChanged"/> for progress, call <see cref="CancelAsync"/> to stop and clean up.
/// </summary>
/// <remarks>
/// <see cref="ProgressChanged"/> and <see cref="StatusChanged"/> may be raised outside the
/// renderer's synchronization context; components should use <c>InvokeAsync(StateHasChanged)</c>.
/// </remarks>
public sealed class UploadHandle : IDisposable
{
    private readonly TaskCompletionSource<UploadResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, Task> _cancel;
    private DotNetObjectReference<UploadHandle>? _reference;

    internal UploadHandle(string id, Func<string, Task> cancel)
    {
        Id = id;
        _cancel = cancel;
    }

    /// <summary>Unique id of this upload.</summary>
    public string Id { get; }

    /// <summary>The file being uploaded.</summary>
    public SelectedFile File { get; internal set; } = new("", 0, "");

    /// <summary>Latest reported progress.</summary>
    public UploadProgress Progress { get; private set; }

    /// <summary>Current state.</summary>
    public UploadStatus Status { get; private set; } = UploadStatus.Running;

    /// <summary>Failure message when <see cref="Status"/> is <see cref="UploadStatus.Failed"/>.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Completes with the stored object, faults with <see cref="DirectUploadException"/>, or is
    /// cancelled (<see cref="TaskCanceledException"/>) after <see cref="CancelAsync"/>.
    /// </summary>
    public Task<UploadResult> Completion => _completion.Task;

    /// <summary>Raised (throttled) while bytes are sent.</summary>
    public event Action<UploadProgress>? ProgressChanged;

    /// <summary>Raised once when the upload completes, fails or is cancelled.</summary>
    public event Action<UploadStatus>? StatusChanged;

    /// <summary>Stops all transfers and aborts the multipart upload on the server. No-op when already finished.</summary>
    public Task CancelAsync() => Status == UploadStatus.Running ? _cancel(Id) : Task.CompletedTask;

    internal DotNetObjectReference<UploadHandle> Reference => _reference ??= DotNetObjectReference.Create(this);

    /// <summary>Called from JavaScript.</summary>
    [JSInvokable]
    public void OnProgress(long bytesUploaded, long totalBytes)
    {
        if (Status != UploadStatus.Running) return;
        Progress = new UploadProgress(bytesUploaded, totalBytes);
        ProgressChanged?.Invoke(Progress);
    }

    /// <summary>Called from JavaScript.</summary>
    [JSInvokable]
    public void OnCompleted(UploadResult result)
    {
        if (Status != UploadStatus.Running) return;
        Progress = new UploadProgress(File.Size, File.Size);
        ProgressChanged?.Invoke(Progress);
        Finish(UploadStatus.Completed, () => _completion.TrySetResult(result));
    }

    /// <summary>Called from JavaScript.</summary>
    [JSInvokable]
    public void OnFailed(string message, string? cleanupError)
    {
        if (Status != UploadStatus.Running) return;
        Error = message;
        Finish(UploadStatus.Failed, () => _completion.TrySetException(new DirectUploadException(message, cleanupError)));
    }

    /// <summary>Called from JavaScript.</summary>
    [JSInvokable]
    public void OnCancelled(string? cleanupError)
    {
        if (Status != UploadStatus.Running) return;
        Error = cleanupError;
        Finish(UploadStatus.Cancelled, () => _completion.TrySetCanceled());
    }

    private void Finish(UploadStatus status, Action complete)
    {
        Status = status;
        StatusChanged?.Invoke(status);
        complete();
        Dispose();
    }

    /// <summary>Releases the JS callback reference. Does not cancel the upload; use <see cref="CancelAsync"/>.</summary>
    public void Dispose()
    {
        _reference?.Dispose();
        _reference = null;
    }
}
