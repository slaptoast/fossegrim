using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services.Library;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Tests;

public sealed class FavoritesTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;
    private readonly string _tag = "f" + Guid.NewGuid().ToString("N")[..10];

    public FavoritesTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AddingAndRemoving_IsIdempotent_AndListedByKind()
    {
        var (artist, album, track) = await LibraryAsync("Song");
        var user = await _factory.CreateUserClientAsync();

        foreach (var url in new[] { $"tracks/{track}", $"tracks/{track}", $"albums/{album}", $"artists/{artist}" })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await user.PutAsync($"/api/favorites/{url}", null)).StatusCode);
        }

        var favorites = await user.GetFromJsonAsync<FavoritesDto>("/api/favorites");
        Assert.Equal([track], favorites!.TrackIds);
        Assert.Equal([album], favorites.AlbumIds);
        Assert.Equal([artist], favorites.ArtistIds);

        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/favorites/tracks/{track}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/favorites/tracks/{track}")).StatusCode);
        Assert.Empty((await user.GetFromJsonAsync<FavoritesDto>("/api/favorites"))!.TrackIds);
    }

    [Fact]
    public async Task Favorites_ArePerUser()
    {
        var (_, _, track) = await LibraryAsync("Mine");
        var me = await _factory.CreateUserClientAsync();
        var someoneElse = await _factory.CreateUserClientAsync();

        await me.PutAsync($"/api/favorites/tracks/{track}", null);

        Assert.Empty((await someoneElse.GetFromJsonAsync<FavoritesDto>("/api/favorites"))!.TrackIds);
        Assert.Equal(0, (await someoneElse.GetFromJsonAsync<PagedResult<MediaItemDto>>($"/api/mediaitems?q={_tag}&favorites=true"))!.TotalCount);
    }

    [Theory]
    [InlineData("tracks")]
    [InlineData("albums")]
    [InlineData("artists")]
    public async Task UnknownItems_AreNotFound(string kind)
    {
        var user = await _factory.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await user.PutAsync($"/api/favorites/{kind}/{Guid.NewGuid()}", null)).StatusCode);
    }

    [Fact]
    public async Task UnknownKinds_AreRejected()
    {
        var user = await _factory.CreateUserClientAsync();

        var response = await user.PutAsync($"/api/favorites/playlists/{Guid.NewGuid()}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("kind must be tracks, albums or artists.", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Lists_FilterToFavourites_NewestFirstByDefault()
    {
        var (_, firstAlbum, first) = await LibraryAsync("First");
        var (_, secondAlbum, second) = await LibraryAsync("Second");
        await LibraryAsync("Never");
        var user = await _factory.CreateUserClientAsync();
        await user.PutAsync($"/api/favorites/tracks/{first}", null);
        await user.PutAsync($"/api/favorites/albums/{firstAlbum}", null);
        // CreatedAt has sub-millisecond precision, but make the order certain.
        await Task.Delay(20);
        await user.PutAsync($"/api/favorites/tracks/{second}", null);
        await user.PutAsync($"/api/favorites/albums/{secondAlbum}", null);

        var tracks = await user.GetFromJsonAsync<PagedResult<MediaItemDto>>($"/api/mediaitems?q={_tag}&favorites=true");
        var albums = await user.GetFromJsonAsync<PagedResult<AlbumListItemDto>>($"/api/albums?q={_tag}&favorites=true&order=asc");

        Assert.Equal([$"Second {_tag}", $"First {_tag}"], tracks!.Items.Select(t => t.Title!));
        Assert.Equal([$"First {_tag}", $"Second {_tag}"], albums!.Items.Select(a => a.Name));
    }

    [Fact]
    public async Task FavoritedSort_PutsFavouritesFirst_InTheWholeList()
    {
        var (_, _, plain) = await LibraryAsync("Plain");
        var (_, _, liked) = await LibraryAsync("Liked");
        var user = await _factory.CreateUserClientAsync();
        await user.PutAsync($"/api/favorites/tracks/{liked}", null);

        var tracks = await user.GetFromJsonAsync<PagedResult<MediaItemDto>>($"/api/mediaitems?q={_tag}&sort=favorited");

        Assert.Equal([liked, plain], tracks!.Items.Select(t => t.Id));
    }

    [Fact]
    public async Task RemovingAnItem_RemovesItFromFavourites()
    {
        var (_, _, track) = await LibraryAsync("Doomed");
        var user = await _factory.CreateUserClientAsync();
        await user.PutAsync($"/api/favorites/tracks/{track}", null);

        await _factory.WithDbAsync(db => db.MediaItems.Where(m => m.Id == track).ExecuteDeleteAsync());

        Assert.Empty((await user.GetFromJsonAsync<FavoritesDto>("/api/favorites"))!.TrackIds);
    }

    // An artist with one album of one track, all named "{name} {_tag}".
    private async Task<(Guid Artist, Guid Album, Guid Track)> LibraryAsync(string name)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = $"{name} {_tag}" };
        var album = new Album { Id = Guid.NewGuid(), Name = $"{name} {_tag}" };
        var track = new MediaItem { Id = Guid.NewGuid(), Title = $"{name} {_tag}", ArtistName = artist.Name, Album = album.Name, FileLocation = $"/music/{Guid.NewGuid():N}.mp3" };
        album.Artists.Add(artist);
        track.Albums.Add(album);
        track.Artists.Add(artist);
        await _factory.WithDbAsync(async db =>
        {
            db.MediaItems.Add(track);
            await db.SaveChangesAsync();
            await new SearchIndex(db).RebuildAsync();
        });
        return (artist.Id, album.Id, track.Id);
    }
}
