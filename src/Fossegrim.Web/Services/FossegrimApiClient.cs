using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

/// <summary>
/// The Blazor app's client for Fossegrim.Api - it talks to the same endpoints
/// a mobile app would. AuthorizedHttpMessageHandler attaches the bearer token
/// to every request, so this class just makes plain HTTP calls.
/// </summary>
public class FossegrimApiClient
{
    private readonly HttpClient _httpClient;
    private readonly TokenStore _tokenStore;

    public FossegrimApiClient(HttpClient httpClient, TokenStore tokenStore)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
    }

    public async Task<IReadOnlyList<ArtistDto>> GetArtistsAsync()
    {
        var response = await _httpClient.GetAsync("/api/artists");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ArtistDto>>() ?? [];
    }

    public async Task<LibraryStatsDto?> GetLibraryStatsAsync()
    {
        var response = await _httpClient.GetAsync("/api/library/stats");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LibraryStatsDto>();
    }

    public async Task<string?> GetVersionAsync()
    {
        var response = await _httpClient.GetAsync("/api/version");
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<VersionDto>();
        return dto?.Version;
    }

    public async Task<string> GetStreamUrlAsync(Guid mediaItemId)
    {
        var token = await _tokenStore.GetTokenAsync()
            ?? throw new InvalidOperationException("Cannot build a stream URL without a stored access token.");
        return $"{_httpClient.BaseAddress}stream/{mediaItemId}?access_token={Uri.EscapeDataString(token)}";
    }

    public async Task<IReadOnlyList<AlbumDto>> GetAlbumsAsync()
    {
        var response = await _httpClient.GetAsync("/api/albums");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<AlbumDto>>() ?? [];
    }

    public async Task<string> GetAlbumCoverUrl(Guid albumId)
    {
        var token = await _tokenStore.GetTokenAsync()
            ?? throw new InvalidOperationException("Cannot build a cover URL without a stored access token.");
        return $"{_httpClient.BaseAddress}cover/{albumId}?access_token={Uri.EscapeDataString(token)}";
    }

    public async Task<IReadOnlyList<MediaItemDto>> GetMediaItemsAsync()
    {
        var response = await _httpClient.GetAsync("/api/mediaitems");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MediaItemDto>>() ?? [];
    }

    public async Task<MediaItemDto?> GetMediaItemAsync(Guid id)
    {
        var response = await _httpClient.GetAsync($"/api/mediaitems/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MediaItemDto>();
    }

    public async Task<UpdateMediaItemTagsResponse> UpdateMediaItemTagsAsync(IEnumerable<MediaItemTagUpdate> items)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/mediaitems/tags", new UpdateMediaItemTagsRequest(items));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UpdateMediaItemTagsResponse>()
            ?? throw new ApiException("Update tags succeeded but returned no result.");
    }

    public async Task<IReadOnlyList<MediaFolderDto>> GetMediaFoldersAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/folders");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MediaFolderDto>>() ?? [];
    }

    public async Task<MediaFolderDto?> GetMediaFolderAsync(Guid id)
    {
        var response = await _httpClient.GetAsync($"/api/admin/folders/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MediaFolderDto>();
    }

    public async Task<AddFolderResult> AddMediaFolderAsync(string name, string location)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/admin/folders", new AddMediaFolderRequest(name, location));

        if (response.StatusCode == HttpStatusCode.Created)
        {
            var folder = await response.Content.ReadFromJsonAsync<MediaFolderDto>();
            return new AddFolderResult(true, folder, null);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            return new AddFolderResult(false, null, error?.Error ?? "A folder with this location already exists.");
        }

        return new AddFolderResult(false, null, "Failed to add folder.");
    }

    public async Task DeleteMediaFolderAsync(Guid id)
    {
        var response = await _httpClient.DeleteAsync($"/api/admin/folders/{id}");
        if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<ScanResult> ScanAsync(string folderPath, int maxDegreeOfParallelism)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/admin/scan", new ScanRequest(folderPath, maxDegreeOfParallelism));

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            throw new ApiException(error?.Error ?? $"Scan failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<ScanResult>()
            ?? throw new ApiException("Scan succeeded but returned no result.");
    }

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        return await ReadAuthResultAsync(response, unauthorizedMessage: "Invalid login attempt.");
    }

    public async Task<AuthResult> RegisterAsync(string email, string displayName, string password)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, displayName, password));
        return await ReadAuthResultAsync(response, unauthorizedMessage: "Registration failed.");
    }

    public async Task<IReadOnlyList<string>> ChangePasswordAsync(string oldPassword, string newPassword)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(oldPassword, newPassword));

        if (response.IsSuccessStatusCode)
        {
            return [];
        }

        var errors = await response.Content.ReadFromJsonAsync<IdentityErrorsResponse>();
        return errors?.Errors ?? ["Failed to change password."];
    }

    public async Task<ProfileDto?> GetProfileAsync()
    {
        var response = await _httpClient.GetAsync("/api/auth/me");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<ProfileDto>();
    }

    public async Task<ProfileDto> UpdateProfileAsync(string? displayName, string? phoneNumber)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/auth/me", new UpdateProfileRequest(displayName, phoneNumber));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProfileDto>()
            ?? throw new ApiException("Profile update succeeded but returned no result.");
    }

    // The user's appearance settings document (see AppearancePreferences); "{}" if none saved yet.
    public async Task<string> GetPreferencesJsonAsync()
    {
        var response = await _httpClient.GetAsync("/api/me/preferences");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public async Task SavePreferencesJsonAsync(string json)
    {
        var response = await _httpClient.PutAsync("/api/me/preferences", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadProblemDetailAsync(response) ?? $"Saving failed ({(int)response.StatusCode}).");
        }
    }

    public async Task<LastFmStatusDto> GetLastFmStatusAsync()
    {
        var response = await _httpClient.GetAsync("/api/lastfm");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LastFmStatusDto>()
            ?? throw new ApiException("Last.fm status returned no result.");
    }

    public async Task<string> GetLastFmAuthUrlAsync(string callbackUrl)
    {
        var response = await _httpClient.GetAsync($"/api/lastfm/auth-url?callback={Uri.EscapeDataString(callbackUrl)}");
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<LastFmAuthUrlDto>()
            ?? throw new ApiException("Last.fm auth URL returned no result.");
        return dto.Url;
    }

    public async Task<LastFmStatusDto> LinkLastFmAsync(string token)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/lastfm/session", new LastFmLinkRequest(token));
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadProblemDetailAsync(response) ?? $"Linking Last.fm failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<LastFmStatusDto>()
            ?? throw new ApiException("Linking Last.fm returned no result.");
    }

    public async Task<LastFmStatusDto> UnlinkLastFmAsync()
    {
        var response = await _httpClient.DeleteAsync("/api/lastfm/session");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LastFmStatusDto>()
            ?? throw new ApiException("Unlinking Last.fm returned no result.");
    }

    public async Task<LastFmSettingsDto> GetLastFmSettingsAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/lastfm");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LastFmSettingsDto>()
            ?? throw new ApiException("Last.fm settings returned no result.");
    }

    public async Task<LastFmSettingsDto> UpdateLastFmSettingsAsync(string apiKey, string? apiSecret)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/admin/lastfm", new UpdateLastFmSettingsRequest(apiKey, apiSecret));
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadProblemDetailAsync(response) ?? $"Saving Last.fm settings failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<LastFmSettingsDto>()
            ?? throw new ApiException("Saving Last.fm settings returned no result.");
    }

    public async Task<LastFmSettingsDto> ClearLastFmSettingsAsync()
    {
        var response = await _httpClient.DeleteAsync("/api/admin/lastfm");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LastFmSettingsDto>()
            ?? throw new ApiException("Removing Last.fm settings returned no result.");
    }

    public async Task UpdateLastFmNowPlayingAsync(Guid mediaItemId)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/lastfm/now-playing", new LastFmNowPlayingRequest(mediaItemId));
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<PlaylistDto>> GetPlaylistsAsync()
    {
        var response = await _httpClient.GetAsync("/api/playlists");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<PlaylistDto>>() ?? [];
    }

    public async Task<PlaylistDetailDto?> GetPlaylistAsync(Guid id)
    {
        var response = await _httpClient.GetAsync($"/api/playlists/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistDetailDto>();
    }

    public async Task<PlaylistDetailDto> GetQueueAsync()
    {
        var response = await _httpClient.GetAsync("/api/playlists/queue");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistDetailDto>()
            ?? throw new ApiException("Get queue succeeded but returned no result.");
    }

    public async Task<PlaylistDetailDto> SetQueueAsync(IEnumerable<Guid> mediaItemIds, int? currentIndex)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/playlists/queue", new SetQueueRequest(mediaItemIds, currentIndex));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistDetailDto>()
            ?? throw new ApiException("Set queue succeeded but returned no result.");
    }

    public async Task SetQueueCurrentItemAsync(Guid? itemId)
    {
        var response = await _httpClient.PatchAsJsonAsync("/api/playlists/queue/current", new SetQueueCurrentItemRequest(itemId));
        response.EnsureSuccessStatusCode();
    }

    public async Task RecordPlayAsync(Guid mediaItemId, DateTimeOffset? startedAt = null, TimeSpan? listenedFor = null)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/plays", new RecordPlayRequest(mediaItemId, startedAt, listenedFor));
        response.EnsureSuccessStatusCode();
    }

    public async Task<PlayHistoryPageDto> GetPlayHistoryAsync(string? query = null, int page = 1, int pageSize = 50)
    {
        var url = $"/api/plays?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            url += $"&q={Uri.EscapeDataString(query)}";
        }

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlayHistoryPageDto>()
            ?? throw new ApiException("Play history returned no result.");
    }

    public async Task<PlaylistDto> CreatePlaylistAsync(string name)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest(name));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistDto>()
            ?? throw new ApiException("Create playlist succeeded but returned no result.");
    }

    public async Task<PlaylistDto> UpdatePlaylistAsync(Guid id, string name)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/playlists/{id}", new UpdatePlaylistRequest(name));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistDto>()
            ?? throw new ApiException("Update playlist succeeded but returned no result.");
    }

    public async Task DeletePlaylistAsync(Guid id)
    {
        var response = await _httpClient.DeleteAsync($"/api/playlists/{id}");
        if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<PlaylistDetailDto> AddPlaylistItemsAsync(Guid playlistId, IEnumerable<Guid> mediaItemIds)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/playlists/{playlistId}/items", new AddPlaylistItemsRequest(mediaItemIds));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistDetailDto>()
            ?? throw new ApiException("Add tracks succeeded but returned no result.");
    }

    public async Task RemovePlaylistItemAsync(Guid playlistId, Guid itemId)
    {
        var response = await _httpClient.DeleteAsync($"/api/playlists/{playlistId}/items/{itemId}");
        if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<PlaylistDetailDto> MovePlaylistItemAsync(Guid playlistId, Guid itemId, Guid? afterItemId)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/playlists/{playlistId}/items/{itemId}/move", new MovePlaylistItemRequest(afterItemId));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistDetailDto>()
            ?? throw new ApiException("Move track succeeded but returned no result.");
    }

    public async Task<PlaylistShareDto> SharePlaylistAsync(Guid playlistId, string userId, string shareType)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/playlists/{playlistId}/shares", new SharePlaylistRequest(userId, shareType));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistShareDto>()
            ?? throw new ApiException("Share playlist succeeded but returned no result.");
    }

    // Removes the caller's own access to a playlist shared with them.
    public async Task LeavePlaylistAsync(Guid playlistId)
    {
        var response = await _httpClient.PostAsync($"/api/playlists/{playlistId}/leave", null);
        if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<IReadOnlyList<UserSummaryDto>> SearchUsersAsync(string query)
    {
        var response = await _httpClient.GetAsync($"/api/users/search?q={Uri.EscapeDataString(query)}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<UserSummaryDto>>() ?? [];
    }

    public async Task RevokePlaylistShareAsync(Guid playlistId, string userId)
    {
        var response = await _httpClient.DeleteAsync($"/api/playlists/{playlistId}/shares/{Uri.EscapeDataString(userId)}");
        if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<AuthResult> ReadAuthResultAsync(HttpResponseMessage response, string unauthorizedMessage)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
            return new AuthResult(true, loginResponse, []);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new AuthResult(false, null, [unauthorizedMessage]);
        }

        var errors = await response.Content.ReadFromJsonAsync<IdentityErrorsResponse>();
        return new AuthResult(false, null, errors?.Errors ?? ["An unexpected error occurred."]);
    }

    // Results.Problem bodies are {"detail": "..."}; plain BadRequest bodies are a JSON string.
    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            return json.RootElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Object when json.RootElement.TryGetProperty("detail", out var detail) => detail.GetString(),
                System.Text.Json.JsonValueKind.String => json.RootElement.GetString(),
                _ => null
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
    }
}

public record AddFolderResult(bool Success, MediaFolderDto? Folder, string? ErrorMessage);

public record AuthResult(bool Success, LoginResponse? Response, IReadOnlyList<string> Errors);

public class ApiException : Exception
{
    public ApiException(string message) : base(message)
    {
    }
}
