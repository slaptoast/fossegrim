using Microsoft.EntityFrameworkCore;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Services;
using Fossegrim.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowWeb", policy =>
    {
        policy.WithOrigins(
            "http://localhost:5085",
            "https://localhost:7049"
        )
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
});

// Configure SQLite database
builder.Services.AddDbContext<FossegrimDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=fossegrim.db"));

// Register application services
builder.Services.AddScoped<MediaLibraryService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Enable CORS
app.UseCors("AllowWeb");

// Map all endpoints
app.MapAlbumEndpoints();
app.MapArtistEndpoints();
app.MapMediaItemEndpoints();
app.MapAdminEndpoints();

app.Run();
