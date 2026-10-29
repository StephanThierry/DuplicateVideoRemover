using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using deepduplicates;
using DuplicateVideoRemover.Web.Components;
using Microsoft.AspNetCore.DataProtection;


// Usage: DuplicateVideoRemover.Web [--data <folder>] [--db <folder>] [--urls http://0.0.0.0:5077] [--token <secret>] [--no-browser]
//   --data   folder with settings.json and output             (or DVR_DATA; default: the current folder)
//   --db     folder with deepdupes.db and _screens              (or DVR_DB; default: the data folder)
//   --urls   where to listen                                    (or DVR_URLS; default http://127.0.0.1:5077, this computer only)
//   --token  needed to open the page when listening on the network (or DVR_TOKEN; "off" turns it off).
//            Without one, a token is created and kept in <data>/.token, so it stays the same after restarts.

string? Arg(string name) => DataLocation.Arg(args, name);
string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

string dataFolder = DataLocation.ResolveDataFolder(args, Directory.GetCurrentDirectory());
string? databaseFolder = DataLocation.ResolveDatabaseFolder(args);
string urls = Arg("--urls") ?? Env("DVR_URLS") ?? "http://127.0.0.1:5077";
bool openBrowser = !args.Contains("--no-browser");

// The content root is the app folder (static files); the data folder becomes the current directory
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args.Where(a => a != "--no-browser").ToArray(),
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls(urls);
// Serve the framework files straight from the build output too (dotnet run), not only from a publish
builder.WebHost.UseStaticWebAssets();

var controller = AppController.Open(dataFolder, databaseFolder);
controller.Scanner.ProgramName = "the web app";
builder.Services.AddSingleton(controller);
// Keep the keys that protect forms in the data folder, so a restart (or a new container) doesn't break open pages
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(controller.DataFolder, ".keys")))
    .SetApplicationName("DuplicateVideoRemover");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Listening beyond this computer: the page can delete files, so require a token
bool localOnly = urls.Split(';').All(u => u.Contains("127.0.0.1") || u.Contains("localhost") || u.Contains("[::1]"));
string? token = Arg("--token") ?? Env("DVR_TOKEN") ?? (localOnly ? null : StoredToken(controller.DataFolder));
if (string.Equals(token, "off", StringComparison.OrdinalIgnoreCase)) token = null;

// A token that survives restarts: created once, kept in the data folder
static string StoredToken(string folder)
{
    string file = Path.Combine(folder, ".token");
    if (File.Exists(file)) { var t = File.ReadAllText(file).Trim(); if (t.Length >= 8) return t; }
    string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    File.WriteAllText(file, token);
    return token;
}

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);

if (token != null)
{
    const string cookie = "dvr_token";
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Query["token"] == token)
        {
            ctx.Response.Cookies.Append(cookie, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
            ctx.Response.Redirect(ctx.Request.Path);
            return;
        }
        if (ctx.Request.Cookies[cookie] != token)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("Open the address with ?token=... that the program printed when it started.");
            return;
        }
        await next();
    });
}

app.UseAntiforgery();
app.MapStaticAssets();

// Screenshots: /shots/123_1.png
var shotName = new Regex(@"^\d+_[123]\.png$");
app.MapGet("/shots/{name}", (string name) =>
{
    if (!shotName.IsMatch(name)) return Results.NotFound();
    var path = Path.Combine(DataLocation.ScreenshotFolder, name);
    return File.Exists(path) ? Results.File(path, "image/png", enableRangeProcessing: false) : Results.NotFound();
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

string address = urls.Split(';')[0].Replace("0.0.0.0", Dns.GetHostName()).Replace("[::]", Dns.GetHostName());
string openUrl = token == null ? address : $"{address}/?token={token}";
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine($"Duplicate Video Remover is running. Data folder: {controller.DataFolder}");
    Console.WriteLine($"Database: {DataLocation.DatabasePath}");
    Console.WriteLine($"Open: {openUrl}");
    Console.WriteLine("Press Ctrl+C to stop.");
    if (openBrowser && localOnly)
    {
        try { Process.Start(new ProcessStartInfo(openUrl) { UseShellExecute = true }); }
        catch { /* no browser on this machine (server) - the address is printed above */ }
    }
});

app.Run();
