using Amazon.S3;
using DirectUpload.Contracts;
using DirectUpload.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the DirectUpload server.</summary>
public static class DirectUploadServiceCollectionExtensions
{
    /// <summary>
    /// Registers the upload orchestration. Requires an <see cref="IAmazonS3"/> registered by the app
    /// (for R2/MinIO: <c>ServiceURL</c> + <c>ForcePathStyle</c>). Replace <see cref="IUploadKeyPolicy"/>
    /// or <see cref="IUploadLifecycleHandler"/> by registering your own implementation.
    /// </summary>
    public static IServiceCollection AddDirectUpload(this IServiceCollection services, Action<DirectUploadOptions> configure)
    {
        services.AddOptions<DirectUploadOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.BucketName), "DirectUploadOptions.BucketName is required.")
            .Validate(o => o.PartSize > 0, "DirectUploadOptions.PartSize must be positive.")
            .Validate(o => o.MaxFileSize >= 0, "DirectUploadOptions.MaxFileSize cannot be negative.")
            .Validate(o => o.UrlExpiry > TimeSpan.Zero && o.UrlExpiry <= TimeSpan.FromDays(7),
                "DirectUploadOptions.UrlExpiry must be between 0 and 7 days (SigV4 limit).")
            .Validate(o => o.TokenLifetime > TimeSpan.Zero, "DirectUploadOptions.TokenLifetime must be positive.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new UploadTokenSigner(
            sp.GetRequiredService<IOptions<DirectUploadOptions>>().Value.SigningKey, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IUploadKeyPolicy, DefaultUploadKeyPolicy>();
        services.TryAddSingleton<IUploadLifecycleHandler, NullUploadLifecycleHandler>();
        services.TryAddScoped<DirectUploadService>();
        return services;
    }
}

/// <summary>Endpoint mapping of the DirectUpload server.</summary>
public static class DirectUploadEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>POST {prefix}/start</c>, <c>/part-urls</c>, <c>/complete</c> and <c>/abort</c>.
    /// Chain <c>.RequireAuthorization()</c> on the result unless anonymous uploads are intended.
    /// </summary>
    public static RouteGroupBuilder MapDirectUpload(this IEndpointRouteBuilder endpoints, string prefix = "/uploads")
    {
        var group = endpoints.MapGroup(prefix).WithTags("DirectUpload");

        group.MapPost("/start", (StartUploadRequest body, DirectUploadService service, HttpContext http) =>
            Run(() => service.StartAsync(body, http, http.RequestAborted)));
        group.MapPost("/part-urls", (PartUrlsRequest body, DirectUploadService service, HttpContext http) =>
            Run(() => service.GetPartUrlsAsync(body, http)));
        group.MapPost("/complete", (CompleteUploadRequest body, DirectUploadService service, HttpContext http) =>
            Run(() => service.CompleteAsync(body, http, http.RequestAborted)));
        group.MapPost("/abort", (AbortUploadRequest body, DirectUploadService service, HttpContext http) =>
            Run(async () => { await service.AbortAsync(body, http, http.RequestAborted); return new { aborted = true }; }));

        return group;
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (UploadRequestException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: ex.StatusCode);
        }
    }
}
