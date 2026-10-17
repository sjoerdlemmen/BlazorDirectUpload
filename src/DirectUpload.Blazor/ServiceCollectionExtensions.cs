using DirectUpload.Blazor;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the DirectUpload Blazor client.</summary>
public static class DirectUploadClientServiceCollectionExtensions
{
    /// <summary>Registers <see cref="DirectUploader"/>. Call it in the server project and in the WebAssembly project.</summary>
    public static IServiceCollection AddDirectUploadClient(this IServiceCollection services, Action<DirectUploadClientOptions>? configure = null)
    {
        var builder = services.AddOptions<DirectUploadClientOptions>();
        if (configure is not null) builder.Configure(configure);
        services.AddScoped<DirectUploader>();
        return services;
    }
}
