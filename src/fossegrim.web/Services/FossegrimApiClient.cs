using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fossegrim.Lib.Dtos;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Fossegrim.Web.Services;

/// <summary>
/// The web app's client for Fossegrim.Api - it talks to the same endpoints a
/// mobile app would, forwarding the JWT that Web stored in the caller's auth
/// cookie at login time. Web never mints or validates tokens itself.
/// </summary>
public class FossegrimApiClient
{
    private readonly HttpClient _httpClient;

    public FossegrimApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<ArtistDto>> GetArtistsAsync(HttpContext httpContext)
    {
        var response = await SendAsync(httpContext, HttpMethod.Get, "/api/artists");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ArtistDto>>() ?? [];
    }

    public async Task<string> GetStreamUrlAsync(HttpContext httpContext, Guid mediaItemId)
    {
        var token = await GetAccessTokenAsync(httpContext);
        return $"{_httpClient.BaseAddress}stream/{mediaItemId}?access_token={Uri.EscapeDataString(token)}";
    }

    public async Task<IReadOnlyList<MediaItemDto>> GetMediaItemsAsync(HttpContext httpContext)
    {
        var response = await SendAsync(httpContext, HttpMethod.Get, "/api/mediaitems");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MediaItemDto>>() ?? [];
    }

    public async Task<MediaItemDto?> GetMediaItemAsync(HttpContext httpContext, Guid id)
    {
        var response = await SendAsync(httpContext, HttpMethod.Get, $"/api/mediaitems/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MediaItemDto>();
    }

    public async Task<IReadOnlyList<MediaFolderDto>> GetMediaFoldersAsync(HttpContext httpContext)
    {
        var response = await SendAsync(httpContext, HttpMethod.Get, "/api/admin/folders");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MediaFolderDto>>() ?? [];
    }

    public async Task<MediaFolderDto?> GetMediaFolderAsync(HttpContext httpContext, Guid id)
    {
        var response = await SendAsync(httpContext, HttpMethod.Get, $"/api/admin/folders/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MediaFolderDto>();
    }

    public async Task<AddFolderResult> AddMediaFolderAsync(HttpContext httpContext, string name, string location)
    {
        var response = await SendAsync(httpContext, HttpMethod.Post, "/api/admin/folders", new AddMediaFolderRequest(name, location));

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

    public async Task DeleteMediaFolderAsync(HttpContext httpContext, Guid id)
    {
        var response = await SendAsync(httpContext, HttpMethod.Delete, $"/api/admin/folders/{id}");
        if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<ScanResult> ScanAsync(HttpContext httpContext, string folderPath, int maxDegreeOfParallelism)
    {
        var response = await SendAsync(httpContext, HttpMethod.Post, "/api/admin/scan", new ScanRequest(folderPath, maxDegreeOfParallelism));

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

    public async Task<IReadOnlyList<string>> ChangePasswordAsync(HttpContext httpContext, string oldPassword, string newPassword)
    {
        var response = await SendAsync(httpContext, HttpMethod.Post, "/api/auth/change-password", new ChangePasswordRequest(oldPassword, newPassword));

        if (response.IsSuccessStatusCode)
        {
            return [];
        }

        var errors = await response.Content.ReadFromJsonAsync<IdentityErrorsResponse>();
        return errors?.Errors ?? ["Failed to change password."];
    }

    public async Task<ProfileDto?> GetProfileAsync(HttpContext httpContext)
    {
        var response = await SendAsync(httpContext, HttpMethod.Get, "/api/auth/me");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<ProfileDto>();
    }

    public async Task<ProfileDto> UpdateProfileAsync(HttpContext httpContext, string? displayName, string? phoneNumber)
    {
        var response = await SendAsync(httpContext, HttpMethod.Put, "/api/auth/me", new UpdateProfileRequest(displayName, phoneNumber));
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

    private async Task<string> GetAccessTokenAsync(HttpContext httpContext)
    {
        return await httpContext.GetTokenAsync("access_token")
            ?? throw new InvalidOperationException("Cannot call Fossegrim.Api without a stored access token.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpContext httpContext, HttpMethod method, string requestUri, object? body = null)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(httpContext));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            throw new ApiUnauthorizedException();
        }

        return response;
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

public class ApiUnauthorizedException : Exception
{
    public ApiUnauthorizedException() : base("The Fossegrim.Api session has expired or is invalid.")
    {
    }
}
