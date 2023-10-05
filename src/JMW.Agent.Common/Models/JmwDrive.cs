namespace JMW.Agent.Common.Models;

public class JmwDrive
{
    public string? Name { get; set; }
    public bool? IsReady { get; set; }
    public string? RootDirectory { get; set; }
    public DriveType? DriveType { get; set; }
    public string? DriveFormat { get; set; }
    public long? AvailableFreeSpace { get; set; }
    public long? TotalFreeSpace { get; set; }
    public long? TotalSize { get; set; }
    public string? VolumeLabel { get; set; }
}
