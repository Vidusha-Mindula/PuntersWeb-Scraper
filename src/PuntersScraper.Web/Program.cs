using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using PuntersScraper.Web.Components;
using PuntersScraper.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var adminOptions = builder.Configuration.GetSection(AdminCredentialsOptions.SectionName).Get<AdminCredentialsOptions>()
    ?? new AdminCredentialsOptions();

if (string.IsNullOrWhiteSpace(adminOptions.Username) || string.IsNullOrWhiteSpace(adminOptions.Password))
{
    throw new InvalidOperationException(
        "Admin:Username / Admin:Password are not configured. This is a shared/remote deployment, so a " +
        "login is required — set them via appsettings.json, an environment variable " +
        "(Admin__Username / Admin__Password), or a secret store before starting. There is no built-in " +
        "default credential.");
}

builder.Services.AddSingleton(adminOptions);
builder.Services.AddSingleton<ScrapeSessionService>();
builder.Services.AddSingleton<DeveloperNoticeService>();
builder.Services.AddSingleton<UpdateAvailabilityService>();
builder.Services.AddHostedService<PeriodicChecksHostedService>();
builder.Services.AddHostedService<AutoScrapeHostedService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

// Plain (non-Blazor) endpoints for sign-in/out: a Blazor Server circuit can't reliably set the
// auth cookie itself (the response is already committed by the time the circuit is live), so
// the login form posts here as a normal HTML form submission instead.
// Posted to "/account/login" rather than "/login" itself: Blazor's Razor Components endpoint
// (the @page "/login" component) also handles POST to its own route for enhanced-form
// support, which collided with a plain minimal API mapped to the same path
// (AmbiguousMatchException) — a distinct path sidesteps that entirely.
app.MapPost("/account/login", async (
    HttpContext http,
    [FromForm] string username,
    [FromForm] string password,
    [FromForm] string? returnUrl) =>
{
    var options = http.RequestServices.GetRequiredService<AdminCredentialsOptions>();
    if (username == options.Username && password == options.Password)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, username) }, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
    }

    return Results.Redirect("/login?error=1");
}).DisableAntiforgery();

app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapGet("/download-zip", async (HttpContext http, ScrapeSessionService session) =>
{
    if (!http.User.Identity?.IsAuthenticated ?? true) return Results.Redirect("/login");
    if (!session.CanExport) return Results.BadRequest("Nothing to export yet — run a scrape first.");

    var settings = WebAppSettings.Load();
    var (zipBytes, _, _, _) = await session.BuildExportAsync(settings);
    return Results.File(zipBytes, "application/zip", $"punters-export-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();
