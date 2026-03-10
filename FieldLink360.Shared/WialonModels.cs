using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace FieldLink360.Shared;

public class WialonUnit
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string ConnectionStatus { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? HardwareType { get; set; }
    public string? Model { get; set; }
    public string? VIN { get; set; }
    public string? LastAddress { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? IMSI { get; set; }
    public string? ICCID { get; set; }
    public Dictionary<string, string> CustomFields { get; set; } = new();
}

public class WialonSearchResponse
{
    public int TotalItemsCount { get; set; }
    public List<WialonUnitItem> Items { get; set; } = new();
}

public class WialonUnitItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    
    [JsonPropertyName("nm")]
    public string Nm { get; set; } = string.Empty;
    
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }
    
    [JsonPropertyName("uid2")]
    public string? Uid2 { get; set; }
    
    [JsonPropertyName("pos")]
    public WialonPos? Pos { get; set; }
    
    [JsonPropertyName("lmsg")]
    public WialonLastMessage? Lmsg { get; set; }
    
    [JsonPropertyName("netconn")]
    public int? NetConn { get; set; } 
    
    [JsonPropertyName("hw")]
    public long? Hw { get; set; } 
    
    [JsonPropertyName("ph")]
    public string? Ph { get; set; }
    
    [JsonPropertyName("flds")]
    public Dictionary<string, WialonCustomField>? Flds { get; set; } 
    
    [JsonPropertyName("pflds")]
    public Dictionary<string, WialonProfileField>? Pflds { get; set; }
}

public class WialonCustomField
{
    public string N { get; set; } = string.Empty; // Name
    public string V { get; set; } = string.Empty; // Value
}

public class WialonPos
{
    [JsonPropertyName("t")]
    public long T { get; set; }
    
    [JsonPropertyName("x")]
    public double X { get; set; }
    
    [JsonPropertyName("y")]
    public double Y { get; set; }
}

public class WialonProfileField
{
    public string N { get; set; } = string.Empty; // Name
    public string V { get; set; } = string.Empty; // Value
}

public class WialonLastMessage
{
    [JsonPropertyName("t")]
    public long T { get; set; } 
    
    [JsonPropertyName("p")]
    public JsonNode? P { get; set; } 
}

public record WialonHardwareType(long Id, string Name);

public class WialonMessageParams
{
    public string? Imei { get; set; }
    public string? Imsi { get; set; }
    public string? Iccid { get; set; }
}

public class WialonUser
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class WialonWizardResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
