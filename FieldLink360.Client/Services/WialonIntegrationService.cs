using FieldLink360.Shared;
using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace FieldLink360.Client.Services;

public class WialonIntegrationService
{
    private readonly HttpClient _httpClient;
    private readonly IJSRuntime _js;
    public string? UserToken { get; private set; }

    public WialonIntegrationService(HttpClient httpClient, IJSRuntime js)
    {
        _httpClient = httpClient;
        _js = js;
    }

    public async Task InitializeAsync()
    {
        UserToken = await _js.InvokeAsync<string?>("localStorage.getItem", "wialon_token");
    }

    public async Task SetUserToken(string token)
    {
        UserToken = token;
        await _js.InvokeVoidAsync("localStorage.setItem", "wialon_token", token);
    }

    public async Task ClearUserToken()
    {
        UserToken = null;
        await _js.InvokeVoidAsync("localStorage.removeItem", "wialon_token");
    }

    // --- Inventory APIs ---
    
    public async Task<SimInventoryItem?> GetSimInventoryInfo(string iccid)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SimInventoryItem>($"/api/inventory/lookup/{iccid}");
        }
        catch { return null; }
    }

    public async Task<List<SimInventoryItem>> SearchInventoryItems(string query)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<SimInventoryItem>>($"/api/inventory/search?query={query}") ?? new List<SimInventoryItem>();
        }
        catch { return new List<SimInventoryItem>(); }
    }

    // --- Wialon APIs ---

    public async Task<List<WialonUnit>> SearchWialonUnits(string query, string? token = null)
    {
        try
        {
            var effectiveToken = token ?? UserToken;
            var url = $"/api/wialon/units?query={query}";
            if (!string.IsNullOrEmpty(effectiveToken)) url += $"&token={effectiveToken}";
            return await _httpClient.GetFromJsonAsync<List<WialonUnit>>(url) ?? new List<WialonUnit>();
        }
        catch { return new List<WialonUnit>(); }
    }

    public async Task<List<string>> GetBillingPlans()
    {
        try
        {
            var url = $"/api/wialon/billing-plans";
            if (!string.IsNullOrEmpty(UserToken)) url += $"?token={UserToken}";
            return await _httpClient.GetFromJsonAsync<List<string>>(url) ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    public async Task<List<WialonHardwareType>> GetHardwareTypes()
    {
        try
        {
            var url = $"/api/wialon/hw-types";
            if (!string.IsNullOrEmpty(UserToken)) url += $"?token={UserToken}";
            return await _httpClient.GetFromJsonAsync<List<WialonHardwareType>>(url) ?? new List<WialonHardwareType>();
        }
        catch { return new List<WialonHardwareType>(); }
    }

    public async Task<bool> CreateWialonUnit(string name, string hwTypeId, string imei)
    {
        try
        {
            var url = $"/api/wialon/unit";
            if (!string.IsNullOrEmpty(UserToken)) url += $"?token={UserToken}";
            var response = await _httpClient.PostAsJsonAsync(url, new { Name = name, HwTypeId = hwTypeId, Imei = imei });
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> RunAccountWizard(string accountName, string plan, string user, string pass)
    {
        try
        {
            var url = $"/api/wialon/wizard/account";
            if (!string.IsNullOrEmpty(UserToken)) url += $"?token={UserToken}";
            var response = await _httpClient.PostAsJsonAsync(url, new { AccountName = accountName, BillingPlan = plan, UserName = user, Password = pass });
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> OnboardToWialon(DeviceOnboardingModel model)
    {
        return await CreateWialonUnit(model.IMEI, "1", model.IMEI);
    }
}
