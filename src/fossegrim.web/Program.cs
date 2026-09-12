using Microsoft.AspNetCore.Authentication.Cookies;
using Fossegrim.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();

// Web has no database of its own - it authenticates by calling Fossegrim.Api's
// /api/auth endpoints and storing the JWT it gets back in this cookie.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(builder.Configuration.GetValue<int>("Jwt:ExpiryMinutes"));
        options.SlidingExpiration = false; // the cookie must never outlive the token it carries
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
    });
builder.Services.AddAuthorization();

// Fossegrim.Api is the sole owner of all data; the web app is just one more
// client of it, same as a mobile app would be.
builder.Services.AddHttpClient<FossegrimApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"]!);
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();
