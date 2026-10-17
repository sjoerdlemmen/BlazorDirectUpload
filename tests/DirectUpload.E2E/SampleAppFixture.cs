using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Playwright;

namespace DirectUpload.E2E;

/// <summary>
/// Runs the sample app against the S3-compatible server from docker-compose.yml and a Chromium browser.
/// Prerequisites: <c>docker compose up -d</c> and <c>pwsh bin/Debug/net10.0/playwright.ps1 install chromium</c>.
/// </summary>
public sealed class SampleAppFixture : IAsyncLifetime
{
    public const string Bucket = "directupload-e2e";
    public const int PartSizeMiB = 5;
    private const string StorageUrl = "http://localhost:9000";

    private Process? _app;
    private IPlaywright? _playwright;
    private readonly StringBuilder _appLog = new();

    public string BaseUrl { get; private set; } = "";
    public IBrowser Browser { get; private set; } = null!;
    public IAmazonS3 S3 { get; } = new AmazonS3Client(
        new BasicAWSCredentials("directupload", "directupload-secret"),
        new AmazonS3Config
        {
            ServiceURL = StorageUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public string AppLog { get { lock (_appLog) return _appLog.ToString(); } }

    public async Task InitializeAsync()
    {
        using (var http = new HttpClient())
        {
            try
            {
                (await http.GetAsync($"{StorageUrl}/health")).EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"S3-compatible storage is not reachable at {StorageUrl}. Run `docker compose up -d` first.", ex);
            }
        }

        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/SampleApp/SampleApp"));
        var start = new ProcessStartInfo("dotnet", $"run --no-build --no-launch-profile --project \"{project}\" --urls {BaseUrl}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Storage__Bucket"] = Bucket;
        start.Environment["DirectUpload__PartSizeMiB"] = PartSizeMiB.ToString();
        _app = Process.Start(start)!;
        _app.OutputDataReceived += (_, e) => { lock (_appLog) _appLog.AppendLine(e.Data); };
        _app.ErrorDataReceived += (_, e) => { lock (_appLog) _appLog.AppendLine(e.Data); };
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();

        using (var http = new HttpClient())
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                if (_app.HasExited)
                    throw new InvalidOperationException($"Sample app exited with {_app.ExitCode}:\n{AppLog}");
                try
                {
                    if ((await http.GetAsync(BaseUrl)).IsSuccessStatusCode) break;
                }
                catch (HttpRequestException)
                {
                }
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Sample app did not start:\n{AppLog}");
                await Task.Delay(500);
            }
        }

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" });
    }

    public async Task<IReadOnlyList<MultipartUpload>> PendingUploadsAsync(string key)
    {
        var response = await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket, Prefix = key });
        return response.MultipartUploads ?? [];
    }

    public async Task<bool> ObjectExistsAsync(string key)
    {
        try
        {
            await S3.GetObjectMetadataAsync(Bucket, key);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        if (_app is { HasExited: false })
        {
            _app.Kill(entireProcessTree: true);
            await _app.WaitForExitAsync();
        }
        S3.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition(Name)]
public sealed class SampleAppCollection : ICollectionFixture<SampleAppFixture>
{
    public const string Name = "sample app";
}
