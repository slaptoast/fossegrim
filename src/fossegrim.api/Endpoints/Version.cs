using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Api.Endpoints;

public static class Version
{
    public static void MapVersionEndpoints(this WebApplication app)
    {
        // Set from the release workflow's git tag via the image's VERSION
        // build-arg (see Dockerfile) -- "dev" for anything built without it,
        // e.g. a local `docker compose up --build`. Public: the footer that
        // displays this loads before login.
        var version = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";

        app.MapGet("/api/version", () => Results.Ok(new VersionDto(version)))
            .WithName("GetVersion")
            .WithOpenApi();
    }
}
