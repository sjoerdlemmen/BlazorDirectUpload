using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace DirectUpload.E2E;

[Collection(SampleAppCollection.Name)]
public sealed partial class UploadTests(SampleAppFixture app) : IAsyncLifetime
{
    private const long MiB = 1024 * 1024;
    private readonly string _dir = Directory.CreateTempSubdirectory("directupload-e2e-").FullName;
    private IBrowserContext _context = null!;
    private IPage _page = null!;
    private readonly ConcurrentQueue<string> _apiCalls = new();
    private readonly ConcurrentDictionary<string, string> _keysByFile = new();

    public async Task InitializeAsync()
    {
        _context = await app.Browser.NewContextAsync();
        _page = await _context.NewPageAsync();
        _page.Request += (_, r) =>
        {
            if (r.Url.Contains("/uploads/")) _apiCalls.Enqueue($"{r.Url[(r.Url.IndexOf("/uploads/") + 9)..]} {r.PostData}");
        };
        _page.Response += async (_, r) =>
        {
            if (!r.Url.EndsWith("/uploads/start") || !r.Ok) return;
            var request = JsonDocument.Parse(r.Request.PostData!).RootElement;
            var body = JsonDocument.Parse(await r.TextAsync()).RootElement;
            _keysByFile[request.GetProperty("fileName").GetString()!] = body.GetProperty("key").GetString()!;
        };
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("", null)]                    // Auto (Server first, WebAssembly once downloaded)
    [InlineData("server", "Server")]
    [InlineData("wasm", "WebAssembly")]
    public async Task Happy_path_uploads_12_MiB_as_three_parts_in_every_render_mode(string path, string? expectedRenderer)
    {
        var file = CreateFile("twelve.bin", 12 * MiB);
        await OpenAsync(path, expectedRenderer);

        await _page.GetByTestId("file-input").SetInputFilesAsync(file);

        var upload = Upload("twelve.bin");
        await Expect(upload).ToHaveAttributeAsync("data-status", "Completed", new() { Timeout = 60_000 });
        await Expect(upload.GetByTestId("percent")).ToHaveTextAsync("100%");
        var key = await upload.GetByTestId("result-key").InnerTextAsync();

        // 12 MiB with 5 MiB parts: 3 parts, the last one 2 MiB.
        Assert.Single(_apiCalls, c => c.StartsWith("complete"));
        Assert.Contains("\"partNumber\":3", _apiCalls.Single(c => c.StartsWith("complete")));
        Assert.DoesNotContain("\"partNumber\":4", _apiCalls.Single(c => c.StartsWith("complete")));

        using var stored = await app.S3.GetObjectAsync(SampleAppFixture.Bucket, key);
        Assert.Equal(12 * MiB, stored.ContentLength);
        Assert.Equal(Sha256(File.OpenRead(file)), Sha256(stored.ResponseStream));
    }

    [Fact]
    public async Task Zero_byte_file_uploads()
    {
        var file = CreateFile("empty.txt", 0);
        await OpenAsync("server", "Server");

        await _page.GetByTestId("file-input").SetInputFilesAsync(file);

        var upload = Upload("empty.txt");
        await Expect(upload).ToHaveAttributeAsync("data-status", "Completed", new() { Timeout = 30_000 });
        var key = await upload.GetByTestId("result-key").InnerTextAsync();
        Assert.True(await app.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task Cancel_stops_the_upload_and_aborts_the_multipart_upload()
    {
        var file = CreateFile("cancel-me.bin", 30 * MiB);
        await SlowDownStorageAsync();
        await OpenAsync("server", "Server");

        await _page.GetByTestId("file-input").SetInputFilesAsync(file);
        var upload = Upload("cancel-me.bin");
        await Expect(upload).ToHaveAttributeAsync("data-status", "Running");
        await WaitForKeyAsync("cancel-me.bin");
        await upload.GetByTestId("cancel").ClickAsync();

        await Expect(upload).ToHaveAttributeAsync("data-status", "Cancelled", new() { Timeout = 30_000 });
        var key = _keysByFile["cancel-me.bin"];
        Assert.Contains(_apiCalls, c => c.StartsWith("abort"));
        Assert.DoesNotContain(_apiCalls, c => c.StartsWith("complete"));
        Assert.Empty(await app.PendingUploadsAsync(key));
        Assert.False(await app.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task Expired_url_403_is_refreshed_transparently()
    {
        var file = CreateFile("refresh.bin", 12 * MiB);
        var rejected = 0;
        await _page.RouteAsync(StorageParts, async route =>
        {
            if (PartNumber(route.Request) == 2 && Interlocked.Exchange(ref rejected, 1) == 0)
                await route.FulfillAsync(new() { Status = 403, Body = "<Error><Code>AccessDenied</Code><Message>Request has expired</Message></Error>", Headers = Cors });
            else
                await route.ContinueAsync();
        });
        await OpenAsync("wasm", "WebAssembly");

        await _page.GetByTestId("file-input").SetInputFilesAsync(file);

        var upload = Upload("refresh.bin");
        await Expect(upload).ToHaveAttributeAsync("data-status", "Completed", new() { Timeout = 60_000 });
        Assert.Equal(1, rejected);
        Assert.Contains(_apiCalls, c => c.StartsWith("part-urls") && c.Contains("\"partNumbers\":[2]"));
        using var stored = await app.S3.GetObjectAsync(SampleAppFixture.Bucket, await upload.GetByTestId("result-key").InnerTextAsync());
        Assert.Equal(Sha256(File.OpenRead(file)), Sha256(stored.ResponseStream));
    }

    [Fact]
    public async Task Permanently_failing_part_fails_the_upload_loudly_and_aborts()
    {
        var file = CreateFile("doomed.bin", 12 * MiB);
        await _page.RouteAsync(StorageParts, async route =>
        {
            if (PartNumber(route.Request) == 2)
                await route.FulfillAsync(new() { Status = 403, Body = "<Error><Code>AccessDenied</Code></Error>", Headers = Cors });
            else
                await route.ContinueAsync();
        });
        await OpenAsync("server", "Server");

        await _page.GetByTestId("file-input").SetInputFilesAsync(file);

        var upload = Upload("doomed.bin");
        await Expect(upload).ToHaveAttributeAsync("data-status", "Failed", new() { Timeout = 60_000 });
        await Expect(upload.GetByTestId("error")).ToContainTextAsync("Part 2 failed after 5 attempts");
        var key = _keysByFile["doomed.bin"];
        Assert.Contains(_apiCalls, c => c.StartsWith("abort"));
        Assert.DoesNotContain(_apiCalls, c => c.StartsWith("complete"));
        Assert.Empty(await app.PendingUploadsAsync(key));
        Assert.False(await app.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task Missing_etag_header_fails_with_a_cors_hint()
    {
        var file = CreateFile("no-etag.bin", 1 * MiB);
        await _page.RouteAsync(StorageParts, route =>
            route.FulfillAsync(new() { Status = 200, Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" } }));
        await OpenAsync("server", "Server");

        await _page.GetByTestId("file-input").SetInputFilesAsync(file);

        var upload = Upload("no-etag.bin");
        await Expect(upload).ToHaveAttributeAsync("data-status", "Failed", new() { Timeout = 30_000 });
        await Expect(upload.GetByTestId("error")).ToContainTextAsync("ExposeHeaders");
        Assert.Empty(await app.PendingUploadsAsync(_keysByFile["no-etag.bin"]));
    }

    [Theory]
    [InlineData("server", "Server")]
    [InlineData("wasm", "WebAssembly")]
    public async Task Two_concurrent_uploads_cancelling_one_leaves_the_other_intact(string path, string expectedRenderer)
    {
        var first = CreateFile("first.bin", 20 * MiB);
        var second = CreateFile("second.bin", 20 * MiB);
        await SlowDownStorageAsync();
        await OpenAsync(path, expectedRenderer);

        await _page.GetByTestId("file-input").SetInputFilesAsync([first, second]);
        await WaitForKeyAsync("first.bin");
        await WaitForKeyAsync("second.bin");
        await Upload("first.bin").GetByTestId("cancel").ClickAsync();

        await Expect(Upload("first.bin")).ToHaveAttributeAsync("data-status", "Cancelled", new() { Timeout = 30_000 });
        await Expect(Upload("second.bin")).ToHaveAttributeAsync("data-status", "Completed", new() { Timeout = 90_000 });
        Assert.Empty(await app.PendingUploadsAsync(_keysByFile["first.bin"]));
        Assert.False(await app.ObjectExistsAsync(_keysByFile["first.bin"]));
        using var stored = await app.S3.GetObjectAsync(SampleAppFixture.Bucket, _keysByFile["second.bin"]);
        Assert.Equal(Sha256(File.OpenRead(second)), Sha256(stored.ResponseStream));
    }

    /// <summary>Opt-in: set DIRECTUPLOAD_LARGE_FILE_GB (e.g. 3) to upload a file of that size.</summary>
    [SkippableLargeFileFact]
    public async Task Large_file_uploads()
    {
        var gib = long.Parse(Environment.GetEnvironmentVariable("DIRECTUPLOAD_LARGE_FILE_GB")!);
        var file = CreateFile("large.bin", gib * 1024 * MiB);
        await OpenAsync("server", "Server");

        await _page.GetByTestId("file-input").SetInputFilesAsync(file);

        var upload = Upload("large.bin");
        await Expect(upload).ToHaveAttributeAsync("data-status", "Completed", new() { Timeout = 60 * 60_000 });
        var meta = await app.S3.GetObjectMetadataAsync(SampleAppFixture.Bucket, await upload.GetByTestId("result-key").InnerTextAsync());
        Assert.Equal(gib * 1024 * MiB, meta.ContentLength);
    }

    private static readonly Regex StorageParts = new(@"/directupload-e2e/.*[?&]partNumber=\d+");
    private static readonly Dictionary<string, string> Cors = new() { ["Access-Control-Allow-Origin"] = "*" };

    private static int PartNumber(IRequest request) =>
        int.Parse(Regex.Match(request.Url, @"[?&]partNumber=(\d+)").Groups[1].Value);

    private Task SlowDownStorageAsync() => _page.RouteAsync(StorageParts, async route =>
    {
        await Task.Delay(1500);
        try
        {
            await route.ContinueAsync();
        }
        catch (PlaywrightException)
        {
            // The browser aborted the request (cancel) while we were holding it.
        }
    });

    private async Task OpenAsync(string path, string? expectedRenderer)
    {
        await _page.GotoAsync($"{app.BaseUrl}/{path}");
        await Expect(_page.Locator("[data-testid=file-input]:not([disabled])")).ToBeVisibleAsync(new() { Timeout = 60_000 });
        if (expectedRenderer is not null)
            await Expect(_page.GetByTestId("render-mode")).ToContainTextAsync(expectedRenderer);
    }

    private async Task WaitForKeyAsync(string fileName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!_keysByFile.ContainsKey(fileName))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Upload of {fileName} never started.");
            await Task.Delay(100);
        }
    }

    private ILocator Upload(string fileName) => _page.Locator($"[data-testid=upload][data-file='{fileName}']");

    private string CreateFile(string name, long size)
    {
        var path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        var buffer = new byte[Math.Min(size, 4 * MiB)];
        for (long written = 0; written < size; written += buffer.Length)
        {
            Random.Shared.NextBytes(buffer);
            stream.Write(buffer, 0, (int)Math.Min(buffer.Length, size - written));
        }
        return path;
    }

    private static string Sha256(Stream stream)
    {
        using (stream) return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}

public sealed class SkippableLargeFileFactAttribute : FactAttribute
{
    public SkippableLargeFileFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DIRECTUPLOAD_LARGE_FILE_GB")))
            Skip = "Set DIRECTUPLOAD_LARGE_FILE_GB to run the large-file upload.";
        Timeout = 0;
    }
}
