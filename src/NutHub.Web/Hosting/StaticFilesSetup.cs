using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace NutHub.Web.Hosting;

/// <summary>
/// Serves the single-page application embedded in this assembly. index.html is revalidated on every visit so an
/// update of NutHub shows at once; the other files are cached briefly and revalidated with ETag / Last-Modified.
/// </summary>
internal static class StaticFilesSetup
{
    public const string IndexCacheControl = "no-cache";
    public const string AssetCacheControl = "public, max-age=300";

    public static IFileProvider CreateProvider()
    {
        try
        {
            return new ManifestEmbeddedFileProvider(typeof(StaticFilesSetup).Assembly, "wwwroot");
        }
        catch (InvalidOperationException)
        {
            // Built without the embedded manifest (no wwwroot): the API still works.
            return new NullFileProvider();
        }
    }

    public static FileExtensionContentTypeProvider ContentTypes()
    {
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".html"] = "text/html; charset=utf-8";
        types.Mappings[".htm"] = "text/html; charset=utf-8";
        types.Mappings[".js"] = "text/javascript; charset=utf-8";
        types.Mappings[".mjs"] = "text/javascript; charset=utf-8";
        types.Mappings[".css"] = "text/css; charset=utf-8";
        types.Mappings[".json"] = "application/json; charset=utf-8";
        types.Mappings[".map"] = "application/json; charset=utf-8";
        types.Mappings[".svg"] = "image/svg+xml";
        types.Mappings[".webmanifest"] = "application/manifest+json";
        types.Mappings[".woff2"] = "font/woff2";
        types.Mappings[".woff"] = "font/woff";
        types.Mappings[".ico"] = "image/x-icon";
        types.Mappings[".png"] = "image/png";
        types.Mappings[".txt"] = "text/plain; charset=utf-8";
        return types;
    }

    public static void UsePanelFiles(this IApplicationBuilder app)
    {
        IFileProvider provider = CreateProvider();
        var defaults = new DefaultFilesOptions { FileProvider = provider };
        defaults.DefaultFileNames.Clear();
        defaults.DefaultFileNames.Add("index.html");
        app.UseDefaultFiles(defaults);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = ContentTypes(),
            ServeUnknownFileTypes = false,
            OnPrepareResponse = context =>
            {
                bool index = string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase);
                context.Context.Response.Headers.CacheControl = index ? IndexCacheControl : AssetCacheControl;
            },
        });
    }
}
