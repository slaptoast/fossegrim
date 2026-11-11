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
    // How this app is listed under the user's signed-in devices.
    public const string ClientName = "Fossegrim Web";

    private readonly TokenStore _tokenStore;
    private readonly SessionRefresher _refresher;

    public FossegrimApiClient(HttpClient httpClient, TokenStore tokenStore, SessionRefresher refresher)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _refresher = refresher;
    }

    // Where the Api is, e.g. for showing other applications which URL to call.
    public Uri? BaseAddress => _httpClient.BaseAddress;

    public Task<PagedResult<ArtistListItemDto>> GetArtistsAsync(BrowseQuery query) =>
        GetPageAsync<ArtistListItemDto>("/api/artists", query);

    public Task<PagedResult<AlbumListItemDto>> GetAlbumsAsync(BrowseQuery query) =>
        GetPageAsync<AlbumListItemDto>("/api/albums", query);

    public Task<PagedResult<MediaItemDto>> GetTracksAsync(BrowseQuery query) =>
        GetPageAsync<MediaItemDto>("/api/mediaitems", query);

    // Every track, a page at a time, for the pages that work on the whole
    // library (the playlist editor's track picker, the tag editor).
    public async Task<IReadOnlyList<MediaItemDto>> GetAllMediaItemsAsync()
    {
        var all = new List<MediaItemDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetTracksAsync(new BrowseQuery(Sort: LibrarySorts.Title, Page: page, PageSize: BrowseQuery.MaxPageSize));
            all.AddRange(result.Items);
            if (page >= result.TotalPages)
            {
                return all;
            }
        }
    }

    public async Task<FavoritesDto> GetFavoritesAsync()
    {
        var response = await _httpClient.GetAsync("/api/favorites");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FavoritesDto>() ?? new FavoritesDto([], [], []);
    }

    // kind is one of FavoriteKinds.
    public async Task SetFavoriteAsync(string kind, Guid id, bool favorite)
    {
        var url = $"/api/favorites/{kind}/{id}";
        var response = favorite ? await _httpClient.PutAsync(url, null) : await _httpClient.DeleteAsync(url);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<MediaItemDto>> GetArtistTracksAsync(Guid artistId)
    {
        var response = await _httpClient.GetAsync($"/api/artists/{artistId}/tracks");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MediaItemDto>>() ?? [];
    }

    private async Task<PagedResult<T>> GetPageAsync<T>(string path, BrowseQuery query)
    {
        var response = await _httpClient.GetAsync(path + query.ToQueryString());
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PagedResult<T>>()
            ?? new PagedResult<T>([], 0, query.Page, query.PageSize);
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

    public async Task<SetupStatusDto> GetSetupStatusAsync()
    {
        var response = await _httpClient.GetAsync("/api/setup");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SetupStatusDto>()
            ?? throw new ApiException("Setup status returned no result.");
    }

    public async Task<AuthResult> CreateFirstAdminAsync(string email, string displayName, string password)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/setup/admin", new CreateFirstAdminRequest(email, displayName, password, ClientName));
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new AuthResult(false, null, [await ReadProblemDetailAsync(response) ?? "Setup has already been done."]);
        }
        return await ReadAuthResultAsync(response, "Creating the account failed.");
    }

    public async Task<SetupStatusDto> CompleteSetupAsync()
    {
        var response = await _httpClient.PostAsync("/api/setup/complete", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SetupStatusDto>()
            ?? throw new ApiException("Finishing setup returned no result.");
    }

    public async Task<SetupStatusDto> SetRegistrationOpenAsync(bool open)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/admin/registration", new RegistrationSettingsRequest(open));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SetupStatusDto>()
            ?? throw new ApiException("Saving registration settings returned no result.");
    }

    public async Task<IReadOnlyList<string>> CreateUserAsync(string email, string displayName, string password, bool isAdmin)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(email, displayName, password, isAdmin));
        if (response.IsSuccessStatusCode)
        {
            return [];
        }

        var errors = await response.Content.ReadFromJsonAsync<IdentityErrorsResponse>();
        return errors?.Errors is { Count: > 0 } list ? list : [$"Creating the account failed ({(int)response.StatusCode})."];
    }

    public async Task<UpdateStatusDto> GetUpdateStatusAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/updates");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UpdateStatusDto>()
            ?? throw new ApiException("Update status returned no result.");
    }

    public async Task<UpdateStatusDto> CheckForUpdatesAsync()
    {
        var response = await _httpClient.PostAsync("/api/admin/updates/check", null);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadProblemDetailAsync(response) ?? $"Checking for updates failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<UpdateStatusDto>()
            ?? throw new ApiException("Checking for updates returned no result.");
    }

    public async Task<UpdateStatusDto> SetUpdateCheckEnabledAsync(bool enabled)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/admin/updates/settings", new UpdateCheckSettingsRequest(enabled));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UpdateStatusDto>()
            ?? throw new ApiException("Saving update check settings returned no result.");
    }

    public async Task<string> GetStreamUrlAsync(Guid mediaItemId)
    {
        // The <audio> element can't refresh the token in its URL itself.
        await _refresher.EnsureMediaTokenFreshAsync();
        var token = await _tokenStore.GetMediaTokenAsync()
            ?? throw new InvalidOperationException("Cannot build a stream URL without a stored media token.");
        return $"{_httpClient.BaseAddress}stream/{mediaItemId}?access_token={Uri.EscapeDataString(token)}";
    }

    // mode is RandomModes.Full or RandomModes.Adjusted. Excluded albums are
    // left out unless that would leave none.
    public async Task<IReadOnlyList<AlbumDto>> GetRandomAlbumsAsync(string mode, int count, IEnumerable<Guid>? excludeIds = null)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/random/albums", new RandomPickRequest(mode, count, excludeIds?.ToList()));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<AlbumDto>>() ?? [];
    }

    // Excluded tracks are left out unless that would leave none.
    public async Task<IReadOnlyList<MediaItemDto>> GetRandomTracksAsync(string mode, int count, IEnumerable<Guid>? excludeIds = null)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/random/tracks", new RandomPickRequest(mode, count, excludeIds?.ToList()));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MediaItemDto>>() ?? [];
    }

    public async Task<IReadOnlyList<MediaItemDto>> GetAlbumTracksAsync(Guid albumId)
    {
        var response = await _httpClient.GetAsync($"/api/albums/{albumId}/tracks");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MediaItemDto>>() ?? [];
    }

    // size: the longest side wanted, in pixels (null for full size; see /cover).
    public async Task<string> GetAlbumCoverUrl(Guid albumId, int? size = null)
    {
        await _refresher.EnsureMediaTokenFreshAsync();
        var token = await _tokenStore.GetMediaTokenAsync()
            ?? throw new InvalidOperationException("Cannot build a cover URL without a stored media token.");
        var sizePart = size is { } px ? $"&size={px}" : "";
        return $"{_httpClient.BaseAddress}cover/{albumId}?access_token={Uri.EscapeDataString(token)}{sizePart}";
    }

    public async Task<IReadOnlyList<CoverProviderStatusDto>> GetCoverProvidersAsync()
    {
        var response = await _httpClient.GetAsync("/api/covers/providers");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<CoverProviderStatusDto>>() ?? [];
    }

    public async Task<CoverSearchResponse> SearchCoversAsync(string album, string? artist)
    {
        var response = await _httpClient.GetAsync(
            $"/api/covers/search?album={Uri.EscapeDataString(album)}&artist={Uri.EscapeDataString(artist ?? "")}");
        return await ReadOrThrowAsync<CoverSearchResponse>(response, "Search");
    }

    // The body is the image itself; the server checks it's a JPEG or PNG.
    public async Task<SetAlbumCoverResponse> UploadAlbumCoverAsync(Guid albumId, byte[] image)
    {
        var response = await _httpClient.PutAsync($"/api/covers/albums/{albumId}", new ByteArrayContent(image));
        return await ReadOrThrowAsync<SetAlbumCoverResponse>(response, "Saving the cover");
    }

    public async Task<SetAlbumCoverResponse> SetAlbumCoverFromSearchAsync(Guid albumId, string imageUrl)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/covers/albums/{albumId}/from-search", new SetCoverFromSearchRequest(imageUrl));
        return await ReadOrThrowAsync<SetAlbumCoverResponse>(response, "Saving the cover");
    }

    // Throws an ApiException with the server's message (from an ApiErrorResponse) on failure.
    private static async Task<T> ReadOrThrowAsync<T>(HttpResponseMessage response, string action)
    {
        await EnsureOkOrThrowAsync(response, action);
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new ApiException($"{action} succeeded but returned no result.");
    }

    // ReadOrThrowAsync for responses with no body.
    private static async Task EnsureOkOrThrowAsync(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? message = null;
        try
        {
            message = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())?.Error;
        }
        catch (Exception)
        {
            // Not an ApiErrorResponse; use the generic message.
        }
        throw new ApiException(message ?? $"{action} failed with status {(int)response.StatusCode}.");
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

    public async Task<ScanStatusDto> GetScanStatusAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/scans");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScanStatusDto>()
            ?? throw new ApiException("Scan status returned no result.");
    }

    public Task<ScanJobDto> StartScanAsync(StartScanRequest request) =>
        PostScanAsync("/api/admin/scans", JsonContent.Create(request));

    public Task<ScanJobDto> RescanAlbumAsync(Guid albumId) => PostScanAsync($"/api/admin/scans/albums/{albumId}", null);

    public Task<ScanJobDto> RescanArtistAsync(Guid artistId) => PostScanAsync($"/api/admin/scans/artists/{artistId}", null);

    private async Task<ScanJobDto> PostScanAsync(string url, HttpContent? content)
    {
        var response = await _httpClient.PostAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadErrorAsync(response) ?? $"Starting the scan failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<ScanJobDto>()
            ?? throw new ApiException("Starting the scan returned no result.");
    }

    public async Task<IReadOnlyList<ApiKeyDto>> GetApiKeysAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/api-keys");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ApiKeyDto>>() ?? [];
    }

    public async Task<CreatedApiKeyDto> CreateApiKeyAsync(string name)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/admin/api-keys", new CreateApiKeyRequest(name));
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadErrorAsync(response) ?? $"Creating the key failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<CreatedApiKeyDto>()
            ?? throw new ApiException("Creating the key returned no result.");
    }

    public async Task RevokeApiKeyAsync(Guid id)
    {
        var response = await _httpClient.DeleteAsync($"/api/admin/api-keys/{id}");
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<RenameTemplateDto> GetRenameTemplateAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/rename/template");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RenameTemplateDto>()
            ?? throw new ApiException("The file-name template returned no result.");
    }

    public async Task<RenameTemplateDto> SaveRenameTemplateAsync(string template)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/admin/rename/template", new UpdateRenameTemplateRequest(template));
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadErrorAsync(response) ?? $"Saving the template failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<RenameTemplateDto>()
            ?? throw new ApiException("Saving the template returned no result.");
    }

    public async Task<RenamePlanDto> PreviewRenameAsync(RenameRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/admin/rename/preview", request);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadErrorAsync(response) ?? $"Previewing failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<RenamePlanDto>()
            ?? throw new ApiException("Previewing returned no result.");
    }

    public async Task<RenameResultDto> RenameAsync(RenameRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/admin/rename", request);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadErrorAsync(response) ?? $"Renaming failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<RenameResultDto>()
            ?? throw new ApiException("Renaming returned no result.");
    }

    public async Task<LibraryHealthDto> GetLibraryHealthAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/library-health");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LibraryHealthDto>()
            ?? throw new ApiException("Library health returned no result.");
    }

    public async Task<IReadOnlyList<TagVersionMismatchDto>> GetTagVersionMismatchesAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/library-health/tag-versions");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<TagVersionMismatchDto>>() ?? [];
    }

    public async Task<FixTagVersionsResultDto> FixTagVersionsAsync(IEnumerable<Guid> mediaItemIds)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/admin/library-health/tag-versions/fix", new FixTagVersionsRequest(mediaItemIds.ToList()));
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadErrorAsync(response) ?? $"Fixing tags failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<FixTagVersionsResultDto>()
            ?? throw new ApiException("Fixing tags returned no result.");
    }

    // ApiErrorResponse ({ "error": ... }) or a problem/plain-string body.
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                json.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        using var copy = new HttpResponseMessage { Content = new StringContent(body) };
        return await ReadProblemDetailAsync(copy);
    }

    // login is an email or a display name.
    public async Task<AuthResult> LoginAsync(string login, string password)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password, ClientName));
        return await ReadAuthResultAsync(response, unauthorizedMessage: "Wrong email, display name or password.");
    }

    public async Task<AuthResult> RegisterAsync(string email, string displayName, string password)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, displayName, password, ClientName));
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new AuthResult(false, null, [await ReadProblemDetailAsync(response) ?? "Registration is closed."]);
        }
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

    public async Task<PasswordResetOptionsDto> GetPasswordResetOptionsAsync() =>
        await _httpClient.GetFromJsonAsync<PasswordResetOptionsDto>("/api/auth/password-reset") ?? new PasswordResetOptionsDto(false);

    // Errors to show, or none when the request went through (whether or not
    // the address has an account; the Api doesn't say).
    public async Task<IReadOnlyList<string>> ForgotPasswordAsync(string email)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(email));
        return response.IsSuccessStatusCode
            ? []
            : [await ReadProblemDetailAsync(response) ?? $"Sending the reset link failed ({(int)response.StatusCode})."];
    }

    public async Task<IReadOnlyList<string>> ResetPasswordAsync(string userId, string token, string newPassword)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, newPassword));
        if (response.IsSuccessStatusCode)
        {
            return [];
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return ["Too many attempts. Wait a minute and try again."];
        }

        try
        {
            var errors = await response.Content.ReadFromJsonAsync<IdentityErrorsResponse>();
            if (errors?.Errors is { Count: > 0 } list)
            {
                return list;
            }
        }
        catch (Exception)
        {
            // Not an IdentityErrorsResponse; use the generic message.
        }
        return [$"Resetting the password failed ({(int)response.StatusCode})."];
    }

    public async Task<IReadOnlyList<AdminUserDto>> GetUsersAsync(bool deleted = false) =>
        await _httpClient.GetFromJsonAsync<List<AdminUserDto>>(deleted ? "/api/admin/users?deleted=true" : "/api/admin/users") ?? [];

    public async Task DeleteUserAsync(string userId) =>
        await EnsureOkOrThrowAsync(
            await _httpClient.DeleteAsync($"/api/admin/users/{Uri.EscapeDataString(userId)}"), "Deleting the account");

    public async Task RestoreUserAsync(string userId) =>
        await EnsureOkOrThrowAsync(
            await _httpClient.PostAsync($"/api/admin/users/{Uri.EscapeDataString(userId)}/restore", null), "Restoring the account");

    public async Task<PasswordResetLinkDto> CreatePasswordResetLinkAsync(string userId) =>
        await ReadOrThrowAsync<PasswordResetLinkDto>(
            await _httpClient.PostAsync($"/api/admin/users/{Uri.EscapeDataString(userId)}/reset-link", null), "Making the reset link");

    public async Task<EmailSettingsDto> GetEmailSettingsAsync() =>
        await ReadOrThrowAsync<EmailSettingsDto>(await _httpClient.GetAsync("/api/admin/email"), "Loading the email settings");

    public async Task<EmailSettingsDto> SaveEmailSettingsAsync(SaveEmailSettingsRequest request) =>
        await ReadOrThrowAsync<EmailSettingsDto>(await _httpClient.PutAsJsonAsync("/api/admin/email", request), "Saving the email settings");

    public async Task ClearEmailSettingsAsync()
    {
        var response = await _httpClient.DeleteAsync("/api/admin/email");
        response.EnsureSuccessStatusCode();
    }

    // Throws an ApiException saying why, when sending fails.
    public async Task SendTestEmailAsync(string to)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/admin/email/test", new TestEmailRequest(to));
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadErrorAsync(response) ?? $"Sending the test email failed ({(int)response.StatusCode}).");
        }
    }

    public async Task<HttpsStatusDto> GetHttpsStatusAsync() =>
        await ReadOrThrowAsync<HttpsStatusDto>(await _httpClient.GetAsync("/api/admin/https"), "Loading the HTTPS status");

    // Saves the settings and starts a certificate request; poll the status for the outcome.
    public async Task<HttpsStatusDto> SaveLetsEncryptAsync(SaveLetsEncryptRequest request) =>
        await ReadOrThrowAsync<HttpsStatusDto>(await _httpClient.PutAsJsonAsync("/api/admin/https/letsencrypt", request), "Requesting the certificate");

    public async Task<HttpsStatusDto> RenewCertificateAsync() =>
        await ReadOrThrowAsync<HttpsStatusDto>(await _httpClient.PostAsync("/api/admin/https/letsencrypt/renew", null), "Renewing the certificate");

    public async Task<HttpsStatusDto> TurnOffLetsEncryptAsync() =>
        await ReadOrThrowAsync<HttpsStatusDto>(await _httpClient.DeleteAsync("/api/admin/https/letsencrypt"), "Turning off Let's Encrypt");

    public async Task<HttpsStatusDto> SetHttpsRedirectAsync(bool enabled) =>
        await ReadOrThrowAsync<HttpsStatusDto>(await _httpClient.PutAsJsonAsync("/api/admin/https/redirect", new HttpsRedirectRequest(enabled)), "Saving the redirect setting");

    // Ends this browser's session on the Api. Best effort: signing out
    // locally goes ahead even if the Api can't be reached.
    public async Task LogoutAsync()
    {
        try
        {
            await _httpClient.PostAsync("/api/auth/logout", null);
        }
        catch (HttpRequestException)
        {
        }
    }

    public async Task<IReadOnlyList<SessionDto>> GetSessionsAsync()
    {
        var response = await _httpClient.GetAsync("/api/auth/sessions");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<SessionDto>>() ?? [];
    }

    public async Task RevokeSessionAsync(Guid sessionId)
    {
        var response = await _httpClient.DeleteAsync($"/api/auth/sessions/{sessionId}");
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task RevokeOtherSessionsAsync()
    {
        var response = await _httpClient.DeleteAsync("/api/auth/sessions");
        response.EnsureSuccessStatusCode();
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
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadErrorAsync(response) ?? $"Updating your profile failed ({(int)response.StatusCode}).");
        }
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

    public async Task<BackupStatusDto> GetBackupStatusAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/backups");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BackupStatusDto>()
            ?? throw new ApiException("Backup status returned no result.");
    }

    public async Task<IReadOnlyList<string>> GetBackupTimeZonesAsync()
    {
        var response = await _httpClient.GetAsync("/api/admin/backups/time-zones");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<string>>() ?? [];
    }

    public async Task<BackupStatusDto> SaveBackupScheduleAsync(BackupScheduleDto schedule)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/admin/backups/schedule", schedule);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadProblemDetailAsync(response) ?? $"Saving the backup schedule failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<BackupStatusDto>()
            ?? throw new ApiException("Saving the backup schedule returned no result.");
    }

    public async Task<BackupStatusDto> RunBackupAsync()
    {
        var response = await _httpClient.PostAsync("/api/admin/backups", null);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadProblemDetailAsync(response) ?? $"Backup failed ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<BackupStatusDto>()
            ?? throw new ApiException("Backup returned no result.");
    }

    public async Task<Stream> DownloadBackupAsync(string fileName)
    {
        var response = await _httpClient.GetAsync($"/api/admin/backups/files/{Uri.EscapeDataString(fileName)}", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync();
    }

    public async Task DeleteBackupAsync(string fileName)
    {
        var response = await _httpClient.DeleteAsync($"/api/admin/backups/files/{Uri.EscapeDataString(fileName)}");
        response.EnsureSuccessStatusCode();
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

        // A locked account (with a message) or the sign-in rate limit (no body).
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new AuthResult(false, null, [await ReadProblemDetailAsync(response) ?? "Too many attempts. Wait a minute and try again."]);
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
