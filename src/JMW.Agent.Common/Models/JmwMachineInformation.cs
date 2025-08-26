using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using JMW.Agent.Common.Linux;

namespace JMW.Agent.Common.Models;

public sealed class JmwMachineInformation
{
    public string? MachineName { get; set; }
    public JmwOperatingSystem? OperatingSystem { get; set; }
    public JmwProcessor? Processor { get; set; }
    public JmwSystemInfo? SystemInfo { get; set; }
    public JmwUserInfo? UserInfo { get; set; }
    public JmwDotNetInfo? DotNetInfo { get; set; }
    public JmwDrive[]? Drives { get; set; }
    public string[]? Printers { get; set; }

    public JmwIpGlobalProperties? IpGlobalProperties { get; set; }
    public JmwNetworkInterface[]? Interfaces { get; set; }
    public JmwNetNeighbor[]? NetNeighbors { get; set; }

    public static JmwMachineInformation GetInfo()
    {
        var env = Environment.GetEnvironmentVariables();

        var processorIdentity = env.Contains("PROCESSOR_IDENTIFIER") ? env["PROCESSOR_IDENTIFIER"]?.ToString() ?? string.Empty : string.Empty;
        var processorLevel = env.Contains("PROCESSOR_LEVEL") ? env["PROCESSOR_LEVEL"]?.ToString() ?? string.Empty : string.Empty;
        var processorRevision = env.Contains("PROCESSOR_REVISION") ? env["PROCESSOR_REVISION"]?.ToString() ?? string.Empty : string.Empty;
        var gcMemoryInfo = GC.GetGCMemoryInfo();

        var drives = DriveInfo.GetDrives().Select(static o => new JmwDrive
        {
            Name = o.Name,
            IsReady = o.IsReady,
            RootDirectory = o.RootDirectory.FullName,
            DriveType = o.DriveType,
            DriveFormat = o.IsReady ? o.DriveFormat : null,
            AvailableFreeSpace = o.IsReady ? o.AvailableFreeSpace : null,
            TotalFreeSpace = o.IsReady ? o.TotalFreeSpace : null,
            TotalSize = o.IsReady ? o.TotalSize : null,
            VolumeLabel = o.IsReady ? o.VolumeLabel : null,
        }).ToArray();

        var ifcs = NetworkInterface.GetAllNetworkInterfaces().Select(static o =>
        {
            var stats = o.GetIPStatistics();
            var ipv4stats = o.GetIPv4Statistics();
            var ifcIpProps = o.GetIPProperties();
            var ifcIpV4Props = ifcIpProps.GetIPv4Properties();
            var ifcIpV6Props = ifcIpProps.GetIPv6Properties();

            return new JmwNetworkInterface
            {
                Id = o.Id,
                Name = o.Name,
                Description = o.Description,
                OperationalStatus = o.OperationalStatus,
                Speed = o.Speed,
                IsReceiveOnly = o.IsReceiveOnly,
                SupportsMulticast = o.SupportsMulticast,
                NetworkInterfaceType = InterfaceTypeLookups.IanaInterfaceType.GetValueOrDefault((int)o.NetworkInterfaceType).Item1,
                NetworkInterfaceTypeDescription = InterfaceTypeLookups.IanaInterfaceType.GetValueOrDefault((int)o.NetworkInterfaceType).Item2,
                PhysicalAddress = o.GetPhysicalAddress(),
                IpInterfaceStatistics = new JmwIpInterfaceStatistics
                {
                    BytesReceived = stats.BytesReceived,
                    BytesSent = stats.BytesSent,
                    IncomingPacketsDiscarded = stats.IncomingPacketsDiscarded,
                    IncomingPacketsWithErrors = stats.IncomingPacketsWithErrors,
                    IncomingUnknownProtocolPackets = !RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? stats.IncomingUnknownProtocolPackets : null,
                    NonUnicastPacketsReceived = stats.NonUnicastPacketsReceived,
                    NonUnicastPacketsSent = !RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? stats.NonUnicastPacketsSent : null,
                    OutgoingPacketsDiscarded = !RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? stats.OutgoingPacketsDiscarded : null,
                    OutgoingPacketsWithErrors = stats.OutgoingPacketsWithErrors,
                    OutputQueueLength = stats.OutputQueueLength,
                    UnicastPacketsReceived = stats.UnicastPacketsReceived,
                    UnicastPacketsSent = stats.UnicastPacketsSent,
                },
                Ipv4InterfaceStatistics = new JmwIpInterfaceStatistics
                {
                    BytesReceived = ipv4stats.BytesReceived,
                    BytesSent = ipv4stats.BytesSent,
                    IncomingPacketsDiscarded = ipv4stats.IncomingPacketsDiscarded,
                    IncomingPacketsWithErrors = ipv4stats.IncomingPacketsWithErrors,
                    IncomingUnknownProtocolPackets = ipv4stats.IncomingUnknownProtocolPackets,
                    NonUnicastPacketsReceived = ipv4stats.NonUnicastPacketsReceived,
                    NonUnicastPacketsSent = ipv4stats.NonUnicastPacketsSent,
                    OutgoingPacketsDiscarded = !RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ipv4stats.OutgoingPacketsDiscarded : null,
                    OutgoingPacketsWithErrors = ipv4stats.OutgoingPacketsWithErrors,
                    OutputQueueLength = ipv4stats.OutputQueueLength,
                    UnicastPacketsReceived = ipv4stats.UnicastPacketsReceived,
                    UnicastPacketsSent = ipv4stats.UnicastPacketsSent,
                },
                IpInterfaceProperties = new JmwIpInterfaceProperties
                {
                    IsDnsEnabled = !RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ifcIpProps.IsDnsEnabled : null,
                    DnsSuffix = ifcIpProps.DnsSuffix,
                    IsDynamicDnsEnabled = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ifcIpProps.IsDynamicDnsEnabled : null,
                    UnicastAddresses = ifcIpProps.UnicastAddresses.Select(static x => new JmwUnicastIpAddressInformation(x)).ToArray(),
                    MulticastAddresses = ifcIpProps.MulticastAddresses.Select(static x => new JmwMulticastIpAddressInformation(x)).ToArray(),
                    AnycastAddresses = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ifcIpProps.AnycastAddresses.Select(static x => new JmwIpAddressInformation(x)).ToArray() : null,
                    DnsAddresses = ifcIpProps.DnsAddresses.Select(static x => new JmwIpAddress(x)).ToArray(),
                    GatewayAddresses = ifcIpProps.GatewayAddresses.Select(static x => new JmwIpAddress(x.Address)).ToArray(),
                    DhcpServerAddresses = !RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ifcIpProps.DhcpServerAddresses.Select(static x => new JmwIpAddress(x)).ToArray() : null,
                    WinsServersAddresses = !RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ifcIpProps.WinsServersAddresses.Select(static x => new JmwIpAddress(x)).ToArray() : null,
                    IPv4Properties = new JmwIpv4InterfaceProperties
                    {
                        UsesWins = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ifcIpV4Props.UsesWins : null,
                        IsDhcpEnabled = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ifcIpV4Props.IsDhcpEnabled : null,
                        IsAutomaticPrivateAddressingActive = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ifcIpV4Props.IsAutomaticPrivateAddressingActive : null,
                        IsAutomaticPrivateAddressingEnabled = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ifcIpV4Props.IsAutomaticPrivateAddressingEnabled : null,
                        Index = ifcIpV4Props.Index,
                        IsForwardingEnabled = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ifcIpV4Props.IsForwardingEnabled : null,
                        Mtu = ifcIpV4Props.Mtu,
                    },
                    IPv6Properties = new JmwIpv6InterfaceProperties
                    {
                        Index = ifcIpV6Props.Index,
                        Mtu = ifcIpV6Props.Mtu,
                    }
                }
            };
        }).ToArray();
        var ipProps = IPGlobalProperties.GetIPGlobalProperties();

        var netNeighbors = Array.Empty<JmwNetNeighbor>();
        var printers = Array.Empty<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var lst = new List<string>();
            #pragma warning disable CA1416 // Validate platform compatibility
            foreach (var p in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            {
                var printerName = p?.ToString();
                if (!string.IsNullOrEmpty(printerName))
                {
                    lst.Add(printerName);
                }
            }
            #pragma warning restore CA1416 // Validate platform compatibility
            printers = lst.ToArray();

            netNeighbors = WindowsService.GetNetNeighbors().Select(static o => new JmwNetNeighbor
            {
                Name = o.Name,
                IPAddress = o.IPAddress is null ? null : new JmwIpAddress(o.IPAddress),
                InterfaceIndex = o.InterfaceIndex,
                InterfaceAlias = o.InterfaceAlias,
                LinkLayerAddress = PhysicalAddress.TryParse(o.LinkLayerAddress, out var physicalAddress) ? physicalAddress : null,
                Store = o.Store,
                State = o.State,
            }).ToArray();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            netNeighbors = LinuxService.ReadArpTable().ToArray();
        }

        var info = new JmwMachineInformation
        {
            MachineName = Environment.MachineName,

            OperatingSystem = new JmwOperatingSystem
            {
                Platform = Environment.OSVersion.Platform,
                ServicePack = Environment.OSVersion.ServicePack,
                Version = Environment.OSVersion.Version,
                VersionString = Environment.OSVersion.VersionString,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                Description = RuntimeInformation.OSDescription,
            },

            DotNetInfo = new JmwDotNetInfo
            {
                Version = Environment.Version,
                FrameworkDescription = RuntimeInformation.FrameworkDescription,
                RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            },

            Processor = new JmwProcessor
            {
                ProcessorCount = Environment.ProcessorCount,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                ProcessorIdentity = processorIdentity,
                ProcessorLevel = processorLevel,
                ProcessorRevision = processorRevision,
            },

            SystemInfo = new JmwSystemInfo
            {
                SystemDateTime = DateTimeOffset.UtcNow,
                SystemPageSize = Environment.SystemPageSize,
                TotalAvailableMemoryBytes = gcMemoryInfo.TotalAvailableMemoryBytes,
            },

            UserInfo = new JmwUserInfo
            {
                UserName = Environment.UserName,
                UserDomainName = Environment.UserDomainName,
            },
            Printers = printers,
            Drives = drives,
            IpGlobalProperties = new JmwIpGlobalProperties
            {
                IsWinsProxy = ipProps.IsWinsProxy,
                DhcpScopeName = ipProps.DhcpScopeName,
                DomainName = ipProps.DomainName,
                HostName = ipProps.HostName,
                NodeType = ipProps.NodeType,
            },

            Interfaces = ifcs,

            NetNeighbors = netNeighbors,
        };
        return info;
    }

    public void Print()
    {
        Console.WriteLine($"{nameof(this.MachineName)}: {this.MachineName}");

        Console.WriteLine($"{nameof(this.OperatingSystem)}");
        Console.WriteLine($"  {nameof(this.OperatingSystem.Platform)}: {this.OperatingSystem?.Platform}");
        Console.WriteLine($"  {nameof(this.OperatingSystem.Version)}: {this.OperatingSystem?.Version}");
        Console.WriteLine($"  {nameof(this.OperatingSystem.VersionString)}: {this.OperatingSystem?.VersionString}");
        Console.WriteLine($"  {nameof(this.OperatingSystem.ServicePack)}: {this.OperatingSystem?.ServicePack}");
        Console.WriteLine($"  {nameof(this.OperatingSystem.Architecture)}: {this.OperatingSystem?.Architecture}");
        Console.WriteLine($"  {nameof(this.OperatingSystem.Description)}: {this.OperatingSystem?.Description}");
        Console.WriteLine();

        Console.WriteLine($"{nameof(this.UserInfo)}");
        Console.WriteLine($"  {nameof(this.UserInfo.UserName)}: {this.UserInfo?.UserName}");
        Console.WriteLine($"  {nameof(this.UserInfo.UserDomainName)}: {this.UserInfo?.UserDomainName}");
        Console.WriteLine();

        Console.WriteLine($"{nameof(this.DotNetInfo)}");
        Console.WriteLine($"  {nameof(this.DotNetInfo.Version)}: {this.DotNetInfo?.Version}");
        Console.WriteLine($"  {nameof(this.DotNetInfo.FrameworkDescription)}: {this.DotNetInfo?.FrameworkDescription}");
        Console.WriteLine($"  {nameof(this.DotNetInfo.RuntimeIdentifier)}: {this.DotNetInfo?.RuntimeIdentifier}");
        Console.WriteLine();

        Console.WriteLine($"{nameof(this.Processor)}");
        Console.WriteLine($"  {nameof(this.Processor.ProcessorCount)}: {this.Processor?.ProcessorCount}");
        Console.WriteLine($"  {nameof(this.Processor.ProcessArchitecture)}: {this.Processor?.ProcessArchitecture}");
        Console.WriteLine($"  {nameof(this.Processor.Is64BitOperatingSystem)}: {this.Processor?.Is64BitOperatingSystem}");
        Console.WriteLine($"  {nameof(this.Processor.ProcessorIdentity)}: {this.Processor?.ProcessorIdentity}");
        Console.WriteLine($"  {nameof(this.Processor.ProcessorLevel)}: {this.Processor?.ProcessorLevel}");
        Console.WriteLine($"  {nameof(this.Processor.ProcessorRevision)}: {this.Processor?.ProcessorRevision}");
        Console.WriteLine();

        Console.WriteLine($"{nameof(this.SystemInfo)}");
        Console.WriteLine($"  {nameof(this.SystemInfo.SystemDateTime)}: {this.SystemInfo?.SystemDateTime}");
        Console.WriteLine($"  {nameof(this.SystemInfo.SystemPageSize)}: {this.SystemInfo?.SystemPageSize}");
        Console.WriteLine($"  {nameof(this.SystemInfo.TotalAvailableMemoryBytes)}: {this.SystemInfo?.TotalAvailableMemoryBytes}");
        Console.WriteLine();

        if (this.Drives?.Any() ?? false)
        {
            Console.WriteLine($"{nameof(this.Drives)}");
            Console.WriteLine("-----------------------------------");
            foreach (var drive in this.Drives)
            {
                PrintProperties(drive, 4);
                Console.WriteLine("    -------------------------------");
            }
            Console.WriteLine();
        }

        if (this.Printers?.Any() ?? false)
        {
            Console.WriteLine($"{nameof(this.Printers)}");
            Console.WriteLine("-----------------------------------");
            foreach (var item in this.Printers)
            {
                PrintProperties(item, 4);
                Console.WriteLine("    -------------------------------");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"{nameof(this.IpGlobalProperties)}");
        Console.WriteLine($"  {nameof(this.IpGlobalProperties.DhcpScopeName)}: {this.IpGlobalProperties?.DhcpScopeName}");
        Console.WriteLine($"  {nameof(this.IpGlobalProperties.DomainName)}: {this.IpGlobalProperties?.DomainName}");
        Console.WriteLine($"  {nameof(this.IpGlobalProperties.HostName)}: {this.IpGlobalProperties?.HostName}");
        Console.WriteLine($"  {nameof(this.IpGlobalProperties.IsWinsProxy)}: {this.IpGlobalProperties?.IsWinsProxy}");
        Console.WriteLine($"  NetBIOS{nameof(this.IpGlobalProperties.NodeType)}: {this.IpGlobalProperties?.NodeType}");
        Console.WriteLine();

        Console.WriteLine($"{nameof(this.Interfaces)}");
        foreach (var ifc in this.Interfaces?.Select(static o => o) ?? [])
        {
            Console.WriteLine("----------------------------------------------");

            Console.WriteLine($"  {nameof(ifc.Id)}: {ifc.Id}");
            Console.WriteLine($"  {nameof(ifc.Name)}: {ifc.Name}");
            Console.WriteLine($"  {nameof(ifc.PhysicalAddress)}: {ifc.PhysicalAddress}");
            Console.WriteLine($"  {nameof(ifc.Description)}: {ifc.Description}");
            Console.WriteLine($"  {nameof(ifc.IsReceiveOnly)}: {ifc.IsReceiveOnly}");
            Console.WriteLine($"  {nameof(ifc.NetworkInterfaceType)}: {ifc.NetworkInterfaceType}");
            Console.WriteLine($"  {nameof(ifc.NetworkInterfaceTypeDescription)}: {ifc.NetworkInterfaceTypeDescription}");
            Console.WriteLine($"  {nameof(ifc.OperationalStatus)}: {ifc.OperationalStatus}");
            Console.WriteLine($"  {nameof(ifc.Speed)}: {ifc.Speed}");
            Console.WriteLine($"  {nameof(ifc.SupportsMulticast)}: {ifc.SupportsMulticast}");

            Console.WriteLine($"  {nameof(ifc.IpInterfaceStatistics)}");
            PrintProperties(ifc.IpInterfaceStatistics, 4);
            Console.WriteLine();

            Console.WriteLine($"  {ifc.Ipv4InterfaceStatistics}");
            PrintProperties(ifc.Ipv4InterfaceStatistics, 4);
            Console.WriteLine();

            Console.WriteLine($"  {ifc.IpInterfaceProperties}");
            PrintProperties(ifc.IpInterfaceProperties, 4);
            Console.WriteLine();

            if (ifc.IpInterfaceProperties?.UnicastAddresses?.Any() ?? false)
            {
                Console.WriteLine($"  {nameof(ifc.IpInterfaceProperties.UnicastAddresses)}");
                foreach (var ipInfo in ifc.IpInterfaceProperties.UnicastAddresses)
                {
                    PrintProperties(ipInfo, 4);
                    Console.WriteLine();
                }
            }

            if (ifc.IpInterfaceProperties?.MulticastAddresses?.Any() ?? false)
            {
                Console.WriteLine($"  {nameof(ifc.IpInterfaceProperties.MulticastAddresses)}");
                foreach (var ipInfo in ifc.IpInterfaceProperties.MulticastAddresses)
                {
                    PrintProperties(ipInfo, 4);
                    Console.WriteLine();
                }
            }

            if (ifc.IpInterfaceProperties?.GatewayAddresses?.Any() ?? false)
            {
                Console.WriteLine($"  {nameof(ifc.IpInterfaceProperties.GatewayAddresses)}");
                foreach (var ipInfo in ifc.IpInterfaceProperties.GatewayAddresses)
                {
                    Console.WriteLine($"    Address: {ipInfo.Address}");
                    Console.WriteLine();
                }
            }

            if (ifc.IpInterfaceProperties?.DnsAddresses?.Any() ?? false)
            {
                Console.WriteLine($"  {nameof(ifc.IpInterfaceProperties.DnsAddresses)}");
                foreach (var ip in ifc.IpInterfaceProperties.DnsAddresses)
                {
                    Console.WriteLine($"    Address: {ip}");
                }
            }

            if (ifc.IpInterfaceProperties?.AnycastAddresses?.Any() ?? false)
            {
                Console.WriteLine($"  {nameof(ifc.IpInterfaceProperties.AnycastAddresses)}");
                foreach (var ipInfo in ifc.IpInterfaceProperties.AnycastAddresses)
                {
                    PrintProperties(ipInfo, 4);
                    Console.WriteLine();
                }
            }

            if (ifc.IpInterfaceProperties?.DhcpServerAddresses?.Any() ?? false)
            {
                Console.WriteLine($"  {nameof(ifc.IpInterfaceProperties.DhcpServerAddresses)}");
                foreach (var ipInfo in ifc.IpInterfaceProperties.DhcpServerAddresses)
                {
                    Console.WriteLine($"    Address: {ipInfo}");
                }
            }

            if (ifc.IpInterfaceProperties?.WinsServersAddresses?.Any() ?? false)
            {
                Console.WriteLine($"  {nameof(ifc.IpInterfaceProperties.WinsServersAddresses)}");
                foreach (var dns in ifc.IpInterfaceProperties.WinsServersAddresses)
                {
                    Console.WriteLine($"    Address: {dns}");
                }
            }
        }
    }

    private static void PrintProperties(object? obj, int indent)
    {
        if (obj is null)
        {
            return;
        }

        if (obj.GetType() == typeof(IPAddress))
        {
            Console.WriteLine($"{string.Empty.PadLeft(indent)}Address: {(IPAddress)obj}");
            return;
        }

        foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(obj))
        {
            if (descriptor.Attributes.OfType<ObsoleteAttribute>().Any())
            {
                continue;
            }

            var value = descriptor.GetValue(obj)?.ToString() ?? string.Empty;
            Console.WriteLine($"{string.Empty.PadLeft(indent)}{descriptor.Name}: {value}");
        }
    }
}
