namespace DirectUpload.Blazor;

/// <summary>Client-side upload settings. Configure defaults with <c>AddDirectUploadClient</c> or pass per upload.</summary>
public sealed class DirectUploadClientOptions
{
    /// <summary>Base path of the endpoints mapped by <c>MapDirectUpload</c>, relative to the document base URI. Default <c>uploads</c>.</summary>
    public string Endpoint { get; set; } = "uploads";

    /// <summary>Parts uploaded in parallel. Default 4.</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>Attempts per part (and per part-URL request) before the whole upload fails. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Minimum interval between progress callbacks into .NET. Default 250 ms (4 per second), which keeps Blazor Server's SignalR traffic low.</summary>
    public int ProgressIntervalMs { get; set; } = 250;

    /// <summary>Number of presigned part URLs requested per round trip. Default 50.</summary>
    public int UrlBatchSize { get; set; } = 50;

    /// <summary>Extra headers sent with calls to the upload endpoints (e.g. <c>Authorization</c> for bearer-token apps). Never sent to storage.</summary>
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

    internal DirectUploadClientOptions Clone() => new()
    {
        Endpoint = Endpoint,
        Concurrency = Concurrency,
        MaxAttempts = MaxAttempts,
        ProgressIntervalMs = ProgressIntervalMs,
        UrlBatchSize = UrlBatchSize,
        Headers = new Dictionary<string, string>(Headers),
    };

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint)) throw new ArgumentException("Endpoint is required.");
        if (Concurrency < 1) throw new ArgumentException("Concurrency must be at least 1.");
        if (MaxAttempts < 1) throw new ArgumentException("MaxAttempts must be at least 1.");
        if (ProgressIntervalMs < 0) throw new ArgumentException("ProgressIntervalMs cannot be negative.");
        if (UrlBatchSize is < 1 or > 1000) throw new ArgumentException("UrlBatchSize must be between 1 and 1000.");
    }
}
