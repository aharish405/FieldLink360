using FieldLink360.Shared;
using System.Net.Http.Json;

namespace FieldLink360.Client.Services;

public class WialonIntegrationService
{
    private readonly HttpClient _httpClient;

    public WialonIntegrationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SimInventoryItem?> GetSimInventoryInfo(string iccid)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SimInventoryItem>($"/api/inventory/lookup/{iccid}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<SimInventoryItem>> SearchInventoryItems(string query)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<SimInventoryItem>>($"/api/inventory/search?query={query}") ?? new List<SimInventoryItem>();
        }
        catch
        {
            return new List<SimInventoryItem>();
        }
    }

    public async Task<bool> OnboardToWialon(DeviceOnboardingModel model)
    {
        // Placeholder method which will eventually call the unit/create_unit Wialon Remote API
        // For now, we'll just simulate a successful onboard
        await Task.Delay(1000);
        return true;
    }
}
