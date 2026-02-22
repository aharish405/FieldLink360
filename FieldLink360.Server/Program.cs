var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapFallbackToFile("index.html");

app.MapGet("/api/inventory/lookup/{iccid}", (string iccid) =>
{
    var path = Path.Combine(app.Environment.ContentRootPath, "Data", "inventory.csv");
    if (!File.Exists(path)) return Results.NotFound("Inventory file not found.");

    var lines = File.ReadAllLines(path);
    foreach (var line in lines.Skip(1))
    {
        var parts = line.Split(',');
        if (parts.Length >= 2 && parts[1].Trim() == iccid)
        {
            return Results.Ok(new { 
                MobileNumber = parts[0].Trim(),
                SimNo = parts[1].Trim(),
                SimImsi = parts.Length > 2 ? parts[2].Trim() : ""
            });
        }
    }
    return Results.NotFound("SIM not found in inventory.");
})
.WithName("LookupSim")
.WithOpenApi();

app.MapGet("/api/inventory/search", (string query) =>
{
    var path = Path.Combine(app.Environment.ContentRootPath, "Data", "inventory.csv");
    if (!File.Exists(path)) return Results.NotFound("Inventory file not found.");

    var lines = File.ReadAllLines(path);
    var results = new List<object>();
    
    // Clean query: remove spaces, dashes, plus signs for a "digits-first" search
    var q = new string(query.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLower();

    foreach (var line in lines.Skip(1))
    {
        var parts = line.Split(',');
        if (parts.Length >= 2)
        {
            var mobile = parts[0].Trim();
            var sim = parts[1].Trim();
            
            // Clean the numbers for comparison
            var mobileClean = new string(mobile.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLower();
            var simClean = new string(sim.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLower();

            if (mobileClean.Contains(q) || simClean.Contains(q))
            {
                results.Add(new { 
                    MobileNumber = mobile,
                    SimNo = sim,
                    SimImsi = parts.Length > 2 ? parts[2].Trim() : ""
                });
            }
        }
    }
    return Results.Ok(results);
})
.WithName("SearchInventory")
.WithOpenApi();

app.Run();

