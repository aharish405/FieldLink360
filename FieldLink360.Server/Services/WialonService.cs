using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FieldLink360.Shared;

namespace FieldLink360.Server.Services;

public class WialonService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private string? _sid;
    private string? _currentToken;
    private long? _userId;
    private DateTime _lastLoginTime;

    public WialonService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    private string GetBaseUrl()
    {
        var host = _configuration["Wialon:Host"] ?? "hst-api.wialon.com";
        return $"https://{host}/wialon/ajax.html";
    }

    private async Task<bool> LoginAsync(string? specificToken = null)
    {
        var token = specificToken ?? _configuration["Wialon:ApiToken"];
        if (string.IsNullOrEmpty(token)) return false;

        if (token != _currentToken)
        {
            _sid = null;
            _currentToken = token;
        }

        var url = $"{GetBaseUrl()}?svc=token/login&params={{\"token\":\"{token}\"}}";
        var response = await _httpClient.GetAsync(url);
        
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                Console.WriteLine($"Wialon Login Error: {error.GetInt32()}");
                return false;
            }

            if (doc.RootElement.TryGetProperty("eid", out var eid))
            {
                _sid = eid.GetString();
                _lastLoginTime = DateTime.UtcNow;

                if (doc.RootElement.TryGetProperty("user", out var user) && user.TryGetProperty("id", out var id))
                {
                    _userId = id.GetInt64();
                    Console.WriteLine($"Wialon Login: Captured UserID {_userId}");
                }
                
                return true;
            }
        }
        return false;
    }

    private async Task EnsureSessionAsync(string? token = null)
    {
        bool forceLogin = (token != null && token != _currentToken);
        if (forceLogin || string.IsNullOrEmpty(_sid) || (DateTime.UtcNow - _lastLoginTime).TotalMinutes > 4)
        {
            await LoginAsync(token);
        }
    }

    private string? GetNodeValue(JsonNode? node, string key)
    {
        if (node == null) return null;
        var obj = node.AsObject();
        foreach(var prop in obj)
        {
            if(prop.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value?.ToString();
            }
        }
        return null;
    }

    private string BuildWialonUrl(string svc, object? parameters = null)
    {
        var baseUrl = GetBaseUrl();
        var url = $"{baseUrl}?svc={svc}";
        if (parameters != null)
        {
            var json = JsonSerializer.Serialize(parameters);
            url += $"&params={Uri.EscapeDataString(json)}";
        }
        if (!string.IsNullOrEmpty(_sid))
        {
            url += $"&sid={_sid}";
        }
        return url;
    }

    public async Task<List<WialonUnit>> SearchUnitsAsync(string query, string? token = null)
    {
        await EnsureSessionAsync(token);
        if (string.IsNullOrEmpty(_sid)) return new List<WialonUnit>();

        var searchParams = new
        {
            spec = new
            {
                itemsType = "avl_unit",
                propName = "sys_name,sys_unique_id",
                propValueMask = $"*{query}*",
                sortType = "sys_name"
            },
            force = 1,
            flags = 10748937,
            from = 0,
            to = 50
        };

        var url = BuildWialonUrl("core/search_items", searchParams);
        var response = await _httpClient.GetAsync(url);
        var content = await response.Content.ReadAsStringAsync();

        if (content.Contains("\"error\":1") || content.Contains("\"error\":7"))
        {
            if (await LoginAsync(token))
            {
                url = BuildWialonUrl("core/search_items", searchParams);
                response = await _httpClient.GetAsync(url);
                content = await response.Content.ReadAsStringAsync();
            }
        }

        if (response.IsSuccessStatusCode)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var searchResponse = JsonSerializer.Deserialize<WialonSearchResponse>(content, options);
            
            return searchResponse?.Items.Select(i => {
                var pNode = i.Lmsg?.P;
                var imei = GetNodeValue(pNode, "imei") ?? i.Uid ?? i.Uid2;
                var imsi = GetNodeValue(pNode, "imsi") ?? GetNodeValue(pNode, "sim_id");
                var iccid = GetNodeValue(pNode, "iccid") ?? GetNodeValue(pNode, "sim_iccid");
                var phone = i.Ph ?? GetNodeValue(pNode, "phone") ?? GetNodeValue(pNode, "mobile");

                var unit = new WialonUnit
                {
                    Id = i.Id,
                    Name = i.Nm,
                    UniqueId = imei ?? "N/A",
                    ConnectionStatus = (i.NetConn == 1 || i.NetConn == 3) ? "Online" : "Offline",
                    LastMessage = i.Lmsg != null 
                        ? DateTimeOffset.FromUnixTimeSeconds(i.Lmsg.T).ToLocalTime().ToString("g")
                        : "No Data",
                    Latitude = i.Pos?.Y,
                    Longitude = i.Pos?.X,
                    PhoneNumber = phone,
                    IMSI = imsi,
                    ICCID = iccid
                };

                if (i.Pflds != null)
                {
                    unit.Model = i.Pflds.Values.FirstOrDefault(p => p.N.Equals("model", StringComparison.OrdinalIgnoreCase))?.V;
                    unit.VIN = i.Pflds.Values.FirstOrDefault(p => p.N.Equals("vin", StringComparison.OrdinalIgnoreCase))?.V;
                    unit.HardwareType = i.Pflds.Values.FirstOrDefault(p => p.N.Equals("device_type", StringComparison.OrdinalIgnoreCase))?.V;
                }

                if (i.Flds != null)
                {
                    foreach (var fld in i.Flds.Values)
                    {
                        if (!string.IsNullOrEmpty(fld.N))
                        {
                            unit.CustomFields[fld.N] = fld.V;
                        }
                    }
                }

                return unit;
            }).ToList() ?? new List<WialonUnit>();
        }

        return new List<WialonUnit>();
    }

    public async Task<List<string>> GetBillingPlansAsync(string? token = null)
    {
        await EnsureSessionAsync(token);
        if (string.IsNullOrEmpty(_sid)) return new List<string> { "Standard", "Basic", "Advanced", "Enterprise" };

        var url = BuildWialonUrl("account/get_billing_plans", new { });
        var (success, content, error) = await CheckWialonResponse(url);

        if (success)
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var plans = new List<string>();
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("name", out var name))
                    {
                        plans.Add(name.GetString() ?? "");
                    }
                }
                if (plans.Any()) return plans;
            }
        }

        return new List<string> { "Standard", "Basic", "Advanced", "Enterprise" };
    }

    public async Task<List<WialonHardwareType>> GetHardwareTypesAsync(string? token = null)
    {
        await EnsureSessionAsync(token);
        if (string.IsNullOrEmpty(_sid)) return new List<WialonHardwareType>();

        var url = BuildWialonUrl("core/get_hw_types", new { });
        var (success, content, error) = await CheckWialonResponse(url);

        if (success)
        {
            try
            {
                var types = JsonSerializer.Deserialize<List<WialonHardwareType>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return types ?? new List<WialonHardwareType>();
            }
            catch { }
        }

        return new List<WialonHardwareType>
        {
            new WialonHardwareType(1, "Wialon Multi-protocol"),
            new WialonHardwareType(11, "Teltonika FMBxxx"),
            new WialonHardwareType(25, "Queclink GVxxx")
        };
    }

    public async Task<bool> CreateUnitAsync(string name, string hwTypeId, string imei, string? token = null)
    {
        await EnsureSessionAsync(token);
        if (string.IsNullOrEmpty(_sid)) return false;

        var createParams = new
        {
            creatorId = _userId ?? 0,
            name = name,
            hwTypeId = long.Parse(hwTypeId),
            dataFlags = 1
        };

        var url = BuildWialonUrl("core/create_unit", createParams);
        var (cSuccess, cContent, cError) = await CheckWialonResponse(url);
        
        if (!cSuccess) return false;

        using var doc = JsonDocument.Parse(cContent);
        if (doc.RootElement.TryGetProperty("item", out var item) && item.TryGetProperty("id", out var id))
        {
            long unitId = id.GetInt64();
            // Step 2: Set unique ID (IMEI) as per the api.json spec process
            var updateParams = new { itemId = unitId, deviceTypeId = long.Parse(hwTypeId), uniqueId = imei };
            var updateUrl = BuildWialonUrl("unit/update_device_type", updateParams);
            await _httpClient.GetAsync(updateUrl);
            return true;
        }

        return false;
    }

    private async Task<(bool Success, string Content, int? ErrorCode)> CheckWialonResponse(string url)
    {
        var response = await _httpClient.GetAsync(url);
        var content = await response.Content.ReadAsStringAsync();
        
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Wialon API Network Error: {response.StatusCode}");
            return (false, content, null);
        }

        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("error", out var errorProp))
        {
            int errorCode = errorProp.GetInt32();
            if (errorCode != 0)
            {
                Console.WriteLine($"Wialon Service Error: {errorCode}");
                return (false, content, errorCode);
            }
        }

        return (true, content, 0);
    }

    private async Task<string?> GetCurrentAccountPlan()
    {
        var url = BuildWialonUrl("core/get_account_data", new { });
        var (success, content, error) = await CheckWialonResponse(url);
        if (success)
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("plan", out var plan))
            {
                return plan.GetString();
            }
        }
        return null;
    }

    public async Task<bool> WizardCreateAccountAsync(string accountName, string billingPlan, string userName, string password, string? token = null)
    {
        await EnsureSessionAsync(token);
        if (string.IsNullOrEmpty(_sid)) return false;

        Console.WriteLine($"Starting Wizard for Account: {accountName}, Plan: {billingPlan}");

        // If it looks like a fallback plan, try to get the actual plan from the current session
        if (billingPlan == "Standard" || billingPlan == "Basic")
        {
            var actualPlan = await GetCurrentAccountPlan();
            if (!string.IsNullOrEmpty(actualPlan))
            {
                Console.WriteLine($"Auto-correcting plan to: {actualPlan}");
                billingPlan = actualPlan;
            }
        }

        // 1. Create Resource
        var resourceParams = new { creatorId = _userId ?? 0, name = accountName, dataFlags = 1L, skipCreatorCheck = 1 };
        var (rSuccess, rContent, rError) = await CheckWialonResponse(BuildWialonUrl("core/create_resource", resourceParams));
        
        if (!rSuccess)
        {
            Console.WriteLine($"Account Wizard Step 1 failed with error {rError}. Response: {rContent}");
            return false;
        }
        
        using var rDoc = JsonDocument.Parse(rContent);
        if (!rDoc.RootElement.TryGetProperty("item", out var rItem) || !rItem.TryGetProperty("id", out var resourceId))
        {
            return false;
        }

        long rId = resourceId.GetInt64();

        // 2. Create User
        var userParams = new { creatorId = _userId ?? 0, name = userName, password = password, dataFlags = 1L };
        var (uSuccess, uContent, uError) = await CheckWialonResponse(BuildWialonUrl("core/create_user", userParams));
        
        if (!uSuccess)
        {
            Console.WriteLine($"Account Wizard Step 2 failed with error {uError}. Response: {uContent}");
            return false;
        }

        // 3. Link into Account
        // Note: account/create_account might need itemId as a long and plan as a string.
        // We ensure URL encoding via BuildWialonUrl.
        var accountParams = new { itemId = rId, plan = billingPlan };
        var (aSuccess, aContent, aError) = await CheckWialonResponse(BuildWialonUrl("account/create_account", accountParams));
        
        if (!aSuccess)
        {
            Console.WriteLine($"Account Wizard Step 3 failed with error {aError}. Response: {aContent}");
            return false;
        }

        return true;
    }
}
