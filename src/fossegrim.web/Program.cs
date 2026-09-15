using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Fossegrim.Web;
using Fossegrim.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
    ?? throw new InvalidOperationException("ApiBaseUrl is not configured.");

builder.Services.AddAuthorizationCore();

// Singleton, not Scoped: IHttpClientFactory builds AuthorizedHttpMessageHandler
// in its own internal DI scope, separate from the app's component scope - a
// Scoped registration here would silently resolve a second, never-populated
// TokenStore/JwtAuthenticationStateProvider inside the handler.
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<JwtAuthenticationStateProvider>();
builder.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthenticationStateProvider>());
builder.Services.AddScoped<PlayerState>();

builder.Services.AddTransient<AuthorizedHttpMessageHandler>();
builder.Services.AddHttpClient<FossegrimApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
})
.AddHttpMessageHandler<AuthorizedHttpMessageHandler>();

await builder.Build().RunAsync();
