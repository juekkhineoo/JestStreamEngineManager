using JestStreamEngineManager.Configuration;
using JestStreamEngineManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace JestStreamEngineManager.Repositories;

/// <summary>
/// Repository for MediaMTX API operations.
/// </summary>
public sealed class MediaMtxRepository(
    HttpClient httpClient,
    IOptions<JestStreamEngineManagerSettings> options,
    ILogger<MediaMtxRepository> logger) : IMediaMtxRepository
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly JestStreamEngineManagerSettings _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<MediaMtxRepository> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<List<string>> GetActivePathsAsync()
    {
        try
        {
            var url = $"{_settings.MediaMtxApiUrl}/v3/paths/list";
            _logger.LogDebug("Fetching MediaMTX paths from: {Url}", url);

            using var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var jsonContent = await response.Content.ReadAsStringAsync();
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var pathsResponse = JsonSerializer.Deserialize<MediaMtxPathsResponse>(jsonContent, opts);

                if (pathsResponse?.Items != null)
                {
                    var activePaths = pathsResponse.Items.Select(p => p.Name).ToList();
                    _logger.LogDebug("Found {Count} paths in MediaMTX: [{Paths}]",
                        activePaths.Count, string.Join(", ", activePaths));
                    return activePaths;
                }
            }
            else
            {
                _logger.LogWarning("MediaMTX API request failed with status: {StatusCode}", response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to MediaMTX API");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "MediaMTX API request timed out");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse MediaMTX API response");
        }

        return [];
    }

    /// <inheritdoc />
    public async Task<MediaMtxAddPathResult> AddPathAsync(string callId, MediaMtxPathConfigRequest pathConfig)
    {
        try
        {
            var url = $"{_settings.MediaMtxApiUrl}/v3/config/paths/add/{Uri.EscapeDataString(callId)}";
            pathConfig.Name = callId;

            var jsonOpts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            var jsonContent = JsonSerializer.Serialize(pathConfig, jsonOpts);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content);

            var result = new MediaMtxAddPathResult { StatusCode = (int)response.StatusCode };

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Added MediaMTX path for CallId: {CallId}", callId);
                result.IsSuccess = true;
                return result;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to add path for CallId {CallId}. Status: {Status}, Response: {Response}",
                callId, response.StatusCode, errorContent);

            try
            {
                var err = JsonSerializer.Deserialize<MediaMtxErrorResponse>(errorContent, jsonOpts);
                result.ErrorMessage = err?.Error ?? errorContent;
            }
            catch (JsonException) { result.ErrorMessage = errorContent; }

            result.IsSuccess = false;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error adding MediaMTX path for CallId: {CallId}", callId);
            return new MediaMtxAddPathResult { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<MediaMtxAddPathResult> RemovePathAsync(string callId)
    {
        try
        {
            var url = $"{_settings.MediaMtxApiUrl}/v3/config/paths/delete/{Uri.EscapeDataString(callId)}";
            using var response = await _httpClient.DeleteAsync(url);

            var result = new MediaMtxAddPathResult { StatusCode = (int)response.StatusCode };

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Removed MediaMTX path for CallId: {CallId}", callId);
                result.IsSuccess = true;
                return result;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to remove path for CallId {CallId}. Status: {Status}, Response: {Response}",
                callId, response.StatusCode, errorContent);

            var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            try
            {
                var err = JsonSerializer.Deserialize<MediaMtxErrorResponse>(errorContent, jsonOpts);
                result.ErrorMessage = err?.Error ?? errorContent;
            }
            catch (JsonException) { result.ErrorMessage = errorContent; }

            result.IsSuccess = false;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error removing MediaMTX path for CallId: {CallId}", callId);
            return new MediaMtxAddPathResult { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
