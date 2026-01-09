namespace HyperXBatteryHID.Devices;

internal sealed record DeviceCandidate(
    string ProtocolId,
    string ProtocolName,
    string DevicePath,
    int VendorId,
    int ProductId,
    string? Manufacturer,
    string? Product,
    string? SerialNumber
)
{
    public string DisplayName
    {
        get
        {
            var baseName = !string.IsNullOrWhiteSpace(Product) ? Product! : $"{VendorId:X4}:{ProductId:X4}";
            if (!string.IsNullOrWhiteSpace(Manufacturer))
                baseName = $"{Manufacturer} {baseName}";
            // DevicePath can be very long; keep it out of UI unless needed.
            return $"{baseName} ({VendorId:X4}:{ProductId:X4})";
        }
    }
}


