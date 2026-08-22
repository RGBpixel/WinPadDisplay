namespace WinPadHost.Models;

public sealed class UsbDeviceInfo
{
    public string? DeviceName { get; set; }
    public string? ProductType { get; set; }
    public string? ProductVersion { get; set; }
    public string? ConnectionType { get; set; }
    public string? Identifier { get; set; }
}
