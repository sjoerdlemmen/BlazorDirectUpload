// Wire contract between DirectUpload.Blazor (browser) and DirectUpload.Server.
// Compiled into both assemblies as internal types, so a host app that
// references both packages never sees duplicate public types. JSON uses
// JsonSerializerDefaults.Web (camelCase, case-insensitive).
namespace DirectUpload.Contracts;

internal sealed class StartUploadRequest
{
    public string FileName { get; set; } = "";
    public string? ContentType { get; set; }
    public long Size { get; set; }
}

internal sealed class StartUploadResponse
{
    public string Token { get; set; } = "";
    public string Key { get; set; } = "";
    public long PartSize { get; set; }
    public int PartCount { get; set; }
}

internal sealed class PartUrlsRequest
{
    public string Token { get; set; } = "";
    public int[] PartNumbers { get; set; } = [];
}

internal sealed class PartUrl
{
    public int PartNumber { get; set; }
    public string Url { get; set; } = "";
}

internal sealed class PartUrlsResponse
{
    public PartUrl[] Urls { get; set; } = [];
}

internal sealed class CompletedPart
{
    public int PartNumber { get; set; }
    public string ETag { get; set; } = "";
}

internal sealed class CompleteUploadRequest
{
    public string Token { get; set; } = "";
    public CompletedPart[] Parts { get; set; } = [];
}

internal sealed class CompleteUploadResponse
{
    public string Key { get; set; } = "";
    public long Size { get; set; }
    public string ETag { get; set; } = "";
}

internal sealed class AbortUploadRequest
{
    public string Token { get; set; } = "";
}
