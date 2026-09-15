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
}

public record AddFolderResult(bool Success, MediaFolderDto? Folder, string? ErrorMessage);

public record AuthResult(bool Success, LoginResponse? Response, IReadOnlyList<string> Errors);

public class ApiException : Exception
{
    public ApiException(string message) : base(message)
    {
    }
}
