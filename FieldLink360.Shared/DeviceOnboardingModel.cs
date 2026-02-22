namespace FieldLink360.Shared;

public enum DeviceModel
{
    Teltonika,
    Concox,
    Queclink,
    Other
}

public class DeviceOnboardingModel
{
    public string IMEI { get; set; } = string.Empty;
    public string SimICCID { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public DeviceModel DeviceModel { get; set; } = DeviceModel.Teltonika;
}
