using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace DirectUpload.Blazor;

/// <summary>
/// Starts uploads from a file input straight to S3-compatible storage. Register with
/// <c>services.AddDirectUploadClient()</c> in both the server and the WebAssembly project.
/// </summary>
public sealed class DirectUploader(IJSRuntime js, IOptions<DirectUploadClientOptions> defaults) : IAsyncDisposable
{
    private const string ModulePath = "./_content/DirectUpload.Blazor/directUpload.js";
    private Task<IJSObjectReference>? _module;

    /// <summary>Files currently selected in <paramref name="input"/> (metadata only).</summary>
    public async Task<IReadOnlyList<SelectedFile>> GetSelectedFilesAsync(ElementReference input)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<SelectedFile[]>("getFiles", input);
    }

    /// <summary>
    /// Starts uploading file <paramref name="fileIndex"/> of <paramref name="input"/>. Returns as soon as
    /// the upload is running; the transfer continues in the browser and reports back to the handle.
    /// </summary>
    /// <exception cref="InvalidOperationException">No file is selected at that index.</exception>
    public async Task<UploadHandle> StartAsync(ElementReference input, int fileIndex = 0, DirectUploadClientOptions? options = null)
    {
        var config = (options ?? defaults.Value).Clone();
        config.Validate();

        var module = await GetModuleAsync();
        var handle = new UploadHandle(Guid.NewGuid().ToString("N"), id => module.InvokeVoidAsync("cancel", id).AsTask());
        try
        {
            handle.File = await module.InvokeAsync<SelectedFile>("start", handle.Id, input, fileIndex, config, handle.Reference);
        }
        catch (JSException ex)
        {
            handle.Dispose();
            throw new InvalidOperationException(ex.Message, ex);
        }
        return handle;
    }

    private Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try
            {
                await (await _module).DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone (Blazor Server).
            }
        }
    }
}
