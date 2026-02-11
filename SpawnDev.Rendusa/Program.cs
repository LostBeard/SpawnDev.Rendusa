using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.WebTorrents;
using SpawnDev.Rendusa;
using SpawnDev.Rendusa.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add BlazorJSRuntime (JavaScript interop)
builder.Services.AddBlazorJSRuntime(out var JS);

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Rendusa services
builder.Services.AddSingleton<MediaLibraryService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<FileImportService>();
builder.Services.AddSingleton<NowPlayingService>();
builder.Services.AddSingleton<VfsServiceWorkerBridge>();

// WebTorrent
builder.Services.AddWebTorrentService();
builder.Services.AddSingleton<WebTorrentManagerService>();

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Start the app using BlazorJSRunAsync
await builder.Build().BlazorJSRunAsync();
