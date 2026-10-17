using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DirectUpload.Server;
using SampleApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// 1. An IAmazonS3 pointed at your storage. The same client works for AWS S3, Cloudflare R2,
//    MinIO-style servers, Backblaze B2 and Wasabi; only the endpoint and credentials differ.
var storage = builder.Configuration.GetSection("Storage");
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    new BasicAWSCredentials(storage["AccessKey"], storage["SecretKey"]),
    new AmazonS3Config
    {
        ServiceURL = storage["ServiceUrl"],
        ForcePathStyle = storage.GetValue("ForcePathStyle", true),
        AuthenticationRegion = storage["Region"] ?? "us-east-1",
        // R2 and several S3-compatible servers reject the default CRC checksums of newer SDKs.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    }));

// 2. The upload endpoints' services.
builder.Services.AddDirectUpload(o =>
{
    o.BucketName = storage["Bucket"]!;
    o.SigningKey = builder.Configuration["DirectUpload:SigningKey"]!;
    o.KeyPrefix = "sample/";
    o.PartSize = builder.Configuration.GetValue("DirectUpload:PartSizeMiB", 16) * PartSizing.MiB;
});

// 3. The Blazor client (also registered in SampleApp.Client for WebAssembly).
builder.Services.AddDirectUploadClient();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    if (storage.GetValue<bool>("EnsureBucket"))
        await EnsureBucketAsync(app.Services.GetRequiredService<IAmazonS3>(), storage["Bucket"]!);
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();

// 4. The endpoints: POST /uploads/start, /part-urls, /complete, /abort.
//    The sample is anonymous; a real app adds .RequireAuthorization().
app.MapDirectUpload("/uploads");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(SampleApp.Client._Imports).Assembly);

app.Run();

// Local development only: create the bucket and the CORS rule browsers need for direct uploads.
// For AWS/R2 configure this once on the bucket instead (see README "Bucket CORS").
static async Task EnsureBucketAsync(IAmazonS3 s3, string bucket)
{
    try
    {
        await s3.PutBucketAsync(bucket);
    }
    catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
    {
    }

    await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
    {
        BucketName = bucket,
        Configuration = new CORSConfiguration
        {
            Rules =
            [
                new CORSRule
                {
                    AllowedOrigins = ["*"],
                    AllowedMethods = ["PUT"],
                    AllowedHeaders = ["*"],
                    ExposeHeaders = ["ETag"],
                    MaxAgeSeconds = 3600,
                },
            ],
        },
    });
}

public partial class Program;
