namespace PuntersScraper.Web.Services;

/// <summary>Login credentials for this shared deployment, bound from the "Admin" configuration
/// section (appsettings.json / environment variables / secrets — never hardcoded here, and
/// never editable from the web UI, unlike <see cref="WebAppSettings"/>). There is one shared
/// login for the whole tool, not per-user accounts — deliberately simple, since this is an
/// internal scraping tool, not a multi-tenant product.</summary>
public sealed class AdminCredentialsOptions
{
    public const string SectionName = "Admin";

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
