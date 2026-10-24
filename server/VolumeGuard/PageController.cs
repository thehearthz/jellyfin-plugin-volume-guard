using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VolumeGuard;

[ApiController]
[Authorize]
public sealed class VolumeGuardPageController : ControllerBase
{
    // Plugin Pages fetches this shell with ApiClient's authenticated session. All
    // settings/status APIs independently enforce administrator access.
    [HttpGet("VolumeGuard/page")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetPage() => Content(Fragment(), "text/html; charset=utf-8");

    public static string Fragment()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.VolumeGuard.Web.config.html")
            ?? throw new InvalidOperationException("Volume Guard settings resource is missing.");
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        var start = html.IndexOf("<body>", StringComparison.Ordinal);
        var end = html.LastIndexOf("</body>", StringComparison.Ordinal);
        if (start < 0 || end < start) throw new InvalidOperationException("Volume Guard settings resource has no body.");
        // Plugin Pages owns the outer router page. Keep a single component and
        // its scoped styles/script, without document tags or nested page routing.
        return html[(start + "<body>".Length)..end].Replace(
            "id=\"volumeGuardPage\" data-role=\"page\" class=\"page type-interior pluginConfigurationPage\"",
            "id=\"volumeGuardPage\" data-vg-plugin-pages=\"true\" class=\"vg-plugin-pages\"",
            StringComparison.Ordinal);
    }
}
