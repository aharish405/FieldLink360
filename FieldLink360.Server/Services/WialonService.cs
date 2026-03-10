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
        var fallbackPlans = new List<string> { "BasicPlan", "BasicPlan1", "svenbyte_mgr", "svenbyteinnovations" };
        if (string.IsNullOrEmpty(_sid)) return fallbackPlans;

        // 1. Try get_account_data (common way)
        long accountId = 0;
        var adUrl = BuildWialonUrl("core/get_account_data", new { });
        var (adSuccess, adContent, adError) = await CheckWialonResponse(adUrl);
        if (adSuccess)
        {
            using var adDoc = JsonDocument.Parse(adContent);
            if (adDoc.RootElement.TryGetProperty("id", out var idNode)) accountId = idNode.GetInt64();
        }

        // 2. If no account ID found, search for resources with billing enabled
        if (accountId == 0)
        {
            var searchParams = new { spec = new { itemsType = "res", propName = "sys_billing", propValue = "*", propType = "property" }, force = 1, flags = 1, from = 0, to = 0 };
            var sUrl = BuildWialonUrl("core/search_items", searchParams);
            var (sSuccess, sContent, sError) = await CheckWialonResponse(sUrl);
            if (sSuccess)
            {
                using var sDoc = JsonDocument.Parse(sContent);
                if (sDoc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
                {
                    accountId = items[0].GetProperty("id").GetInt64();
                }
            }
        }

        // 3. Finally get plans with the ID
        var url = BuildWialonUrl("account/get_billing_plans", new { itemId = accountId });
        var (success, content, error) = await CheckWialonResponse(url);

        if (success)
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var plans = new List<string>();
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        plans.Add(element.GetString() ?? "");
                        continue;
                    }

                    string? val = null;
                    if (element.TryGetProperty("n", out var name)) val = name.GetString();
                    else if (element.TryGetProperty("nm", out var nm)) val = nm.GetString();
                    else if (element.TryGetProperty("name", out var nameProp)) val = nameProp.GetString();
                    
                    if (!string.IsNullOrEmpty(val)) plans.Add(val);
                }
                if (plans.Any()) return plans;
            }
        }

        Console.WriteLine($"GetBillingPlansAsync failed (ID {accountId}, Error {error}), using fallback.");
        return fallbackPlans;
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

    public async Task<List<string?>> GetBusinessSpheresAsync(string? token = null)
    {
        await EnsureSessionAsync(token);
        if (string.IsNullOrEmpty(_sid)) return new List<string?>();

        var url = BuildWialonUrl("account/get_business_spheres", new { });
        var (success, content, error) = await CheckWialonResponse(url);

        if (success)
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.TryGetProperty("n", out var n) ? n.GetString() : null)
                    .Where(n => n != null).ToList();
            }
        }
        return new List<string?> { "General fleets", "Passenger transportation", "Agriculture", "Construction", "Cold chain monitoring" };
    }

    public async Task<List<WialonUser>> SearchUsersAsync(string query = "", string? token = null)
    {
        await EnsureSessionAsync(token);
        if (string.IsNullOrEmpty(_sid)) return new List<WialonUser>();

        var searchParams = new
        {
            spec = new
            {
                itemsType = "user",
                propName = "sys_name",
                propValueMask = $"*{query}*",
                sortType = "sys_name"
            },
            force = 1,
            flags = 1,
            from = 0,
            to = 100
        };

        var url = BuildWialonUrl("core/search_items", searchParams);
        var (success, content, error) = await CheckWialonResponse(url);

        if (success)
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                var users = new List<WialonUser>();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && item.TryGetProperty("nm", out var nm))
                    {
                        users.Add(new WialonUser { Id = id.GetInt64(), Name = nm.GetString() ?? "Unknown" });
                    }
                }
                return users;
            }
        }
        return new List<WialonUser>();
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
            if (doc.RootElement.TryGetProperty("plan", out var plan)) return plan.GetString();
            if (doc.RootElement.TryGetProperty("billing", out var billing) && billing.TryGetProperty("plan", out var bPlan)) return bPlan.GetString();
        }
        return null;
    }

    private string GetWialonErrorMessage(int? errorCode) => errorCode switch
    {
        1 => "Invalid session",
        2 => "Invalid service name",
        3 => "Invalid result",
        4 => "Invalid input",
        5 => "Error performing request",
        6 => "Unknown error",
        7 => "Access denied",
        8 => "Invalid username or password",
        9 => "Authorization error",
        10 => "Too many requests",
        1001 => "No message for selected interval",
        1002 => "Item with such name already exists",
        1003 => "Only one request is allowed at the moment",
        2014 => "Check user creator failed (skipCreatorCheck may be required)",
        _ => $"Wialon Error {errorCode}"
    };

    public async Task<WialonWizardResult> WizardCreateAccountAsync(string accountName, string billingPlan, string userName, string password, string sphere, int mu, string? token = null)
    {
        await EnsureSessionAsync(token);
        if (string.IsNullOrEmpty(_sid)) return new WialonWizardResult { Success = false, ErrorMessage = "No active Wialon session" };

        Console.WriteLine($"Starting Wizard for Account: {accountName}, Plan: {billingPlan}, Sphere: {sphere}");

        // 1. Create User first
        var userParams = new 
        { 
            creatorId = _userId ?? 0, 
            name = userName, 
            password = password, 
            dataFlags = 1L,
            mu = mu,
            skipCreatorCheck = 1
        };
        var (uSuccess, uContent, uError) = await CheckWialonResponse(BuildWialonUrl("core/create_user", userParams));
        
        if (!uSuccess)
        {
            var msg = GetWialonErrorMessage(uError);
            Console.WriteLine($"Account Wizard Step 1 (User) failed: {msg}");
            return new WialonWizardResult { Success = false, ErrorMessage = $"Step 1 (User): {msg}" };
        }

        using var uDoc = JsonDocument.Parse(uContent);
        if (!uDoc.RootElement.TryGetProperty("item", out var uItem) || !uItem.TryGetProperty("id", out var newUserIdNode))
        {
            return new WialonWizardResult { Success = false, ErrorMessage = "Critical: Step 1 succeeded but response was invalid" };
        }
        long newUserId = newUserIdNode.GetInt64();

        // Plan Correction: Try to resolve the dealer's actual plan code if "Standard" or "Basic" labels are used.
        if (billingPlan.Equals("Standard", StringComparison.OrdinalIgnoreCase) || 
            billingPlan.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            var actual = await GetCurrentAccountPlan();
            if (!string.IsNullOrEmpty(actual))
            {
                Console.WriteLine($"Auto-correcting plan from {billingPlan} to {actual}");
                billingPlan = actual;
            }
        }

        // 2. Create Resource (Account Resource)
        // dataFlags: 1 (base) - keeping it minimal
        var resourceParams = new 
        { 
            creatorId = _userId ?? 0, 
            name = accountName, 
            dataFlags = 1L, 
            mu = mu,
            skipCreatorCheck = 1
        };
        var rUrl = BuildWialonUrl("core/create_resource", resourceParams);
        var (rSuccess, rContent, rError) = await CheckWialonResponse(rUrl);
        
        if (!rSuccess)
        {
            var msg = GetWialonErrorMessage(rError);
            Console.WriteLine($"Wizard Step 2 Error: {msg}. Response: {rContent}");
            return new WialonWizardResult { Success = false, ErrorMessage = $"Step 2: {msg}" };
        }
        
        using var rDoc = JsonDocument.Parse(rContent);
        if (!rDoc.RootElement.TryGetProperty("item", out var rItem) || !rItem.TryGetProperty("id", out var resIdNode))
        {
            return new WialonWizardResult { Success = false, ErrorMessage = "Step 2: Success but no ID returned" };
        }
        long resourceId = resIdNode.GetInt64();

        // 3. Convert Resource to Account
        // Try exact plan name first, then lowercase as fallback
        var accountParams = new { itemId = resourceId, plan = billingPlan };
        var aUrl = BuildWialonUrl("account/create_account", accountParams);
        var (aSuccess, aContent, aError) = await CheckWialonResponse(aUrl);
        
        if (!aSuccess && aError == 4)
        {
            // Fallback: try lowercase
            var lowerPlan = billingPlan.ToLower();
            if (lowerPlan != billingPlan)
            {
                Console.WriteLine($"Step 3 failed with 4, retrying with lowercase plan: {lowerPlan}");
                var aUrl2 = BuildWialonUrl("account/create_account", new { itemId = resourceId, plan = lowerPlan });
                (aSuccess, aContent, aError) = await CheckWialonResponse(aUrl2);
            }
        }
        
        if (!aSuccess)
        {
            var msg = GetWialonErrorMessage(aError);
            Console.WriteLine($"Wizard Step 3 Error: {msg} for plan {billingPlan}. Response: {aContent}");
            return new WialonWizardResult { Success = false, ErrorMessage = $"Step 3: {msg}" };
        }

        // 4. Set Business Sphere
        if (!string.IsNullOrEmpty(sphere))
        {
            var sphereParams = new { itemId = resourceId, businessSphere = sphere };
            await CheckWialonResponse(BuildWialonUrl("account/update_business_sphere", sphereParams));
        }

        // 5. Grant absolute rights
        var rightsParams = new { userId = newUserId, itemId = resourceId, rights = -1L };
        await CheckWialonResponse(BuildWialonUrl("user/update_item_access", rightsParams));

        return new WialonWizardResult { Success = true };
    }
}
