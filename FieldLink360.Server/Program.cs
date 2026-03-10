using Microsoft.AspNetCore.ResponseCompression;
using FieldLink360.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// Register Wialon Service
builder.Services.AddHttpClient<WialonService>();
builder.Services.AddScoped<WialonService>();

// Add Cors
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors("AllowAll");

app.MapRazorPages();
app.MapControllers();
app.MapFallbackToFile("index.html");

// Wialon APIs
app.MapGet("/api/wialon/units", async (string query, string? token, WialonService wialon) =>
{
    return Results.Ok(await wialon.SearchUnitsAsync(query, token));
});

app.MapGet("/api/wialon/billing-plans", async (string? token, WialonService wialon) =>
{
    return Results.Ok(await wialon.GetBillingPlansAsync(token));
});

app.MapGet("/api/wialon/hw-types", async (string? token, WialonService wialon) =>
{
    return Results.Ok(await wialon.GetHardwareTypesAsync(token));
});

app.MapPost("/api/wialon/unit", async (WialonUnitCreateRequest req, string? token, WialonService wialon) =>
{
    var success = await wialon.CreateUnitAsync(req.Name, req.HwTypeId, req.Imei, token);
    return success ? Results.Ok() : Results.BadRequest("Failed to create unit.");
});

app.MapPost("/api/wialon/wizard/account", async (WialonAccountWizardRequest req, string? token, WialonService wialon) =>
{
    var success = await wialon.WizardCreateAccountAsync(req.AccountName, req.BillingPlan, req.UserName, req.Password, token);
    return success ? Results.Ok() : Results.BadRequest("Failed to complete account creation wizard.");
});

// Inventory Lookup APIs (restored)
app.MapGet("/api/inventory/lookup/{iccid}", async (string iccid) =>
{
    var path = Path.Combine(app.Environment.ContentRootPath, "Data", "inventory.csv");
    if (!File.Exists(path)) return Results.NotFound("Inventory data file missing.");
    var lines = await File.ReadAllLinesAsync(path);
    foreach (var line in lines.Skip(1))
    {
        var parts = line.Split(',');
        if (parts.Length >= 6 && parts[4].Trim() == iccid)
            return Results.Ok(new { ItemCode = parts[0], Make = parts[1], Model = parts[2], SerialNumber = parts[3], Iccid = parts[4], Pin = parts[5] });
    }
    return Results.NotFound();
});

app.MapGet("/api/inventory/search", async (string query) =>
{
    var path = Path.Combine(app.Environment.ContentRootPath, "Data", "inventory.csv");
    if (!File.Exists(path)) return Results.NotFound();
    var results = new List<object>();
    var lines = await File.ReadAllLinesAsync(path);
    foreach (var line in lines.Skip(1))
    {
        if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            var parts = line.Split(',');
            if (parts.Length >= 6) results.Add(new { ItemCode = parts[0], Make = parts[1], Model = parts[2], SerialNumber = parts[3], Iccid = parts[4], Pin = parts[5] });
        }
    }
    return Results.Ok(results);
});

app.Run();

public record WialonUnitCreateRequest(string Name, string HwTypeId, string Imei);
public record WialonAccountWizardRequest(string AccountName, string BillingPlan, string UserName, string Password);
