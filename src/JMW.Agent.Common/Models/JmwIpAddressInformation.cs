using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace JMW.Agent.Common.Models;

public class JmwMulticastIpAddressInformation : JmwIpAddressInformation
{
    public JmwMulticastIpAddressInformation()
    {
    }

    public JmwMulticastIpAddressInformation(MulticastIPAddressInformation info)
    {
        this.Address = new JmwIpAddress(info.Address);
        this.IsDnsEligible = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.IsDnsEligible : null;
        this.IsTransient = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.IsTransient : null;

        this.AddressPreferredLifetime = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.AddressPreferredLifetime : null;
        this.AddressValidLifetime = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.AddressValidLifetime : null;
        this.DhcpLeaseLifetime = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.DhcpLeaseLifetime : null;
        this.DuplicateAddressDetectionState = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.DuplicateAddressDetectionState : null;
        this.PrefixOrigin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.PrefixOrigin : null;
        this.SuffixOrigin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.SuffixOrigin : null;
    }

    //
    // Summary:
    //     Gets the number of seconds remaining during which this address is the preferred
    //     address.
    //
    // Returns:
    //     An System.Int64 value that specifies the number of seconds left for this address
    //     to remain preferred.
    //
    // Exceptions:
    //   T:System.PlatformNotSupportedException:
    //     This property is not valid on computers running operating systems earlier than
    //     Windows XP.
    public long? AddressPreferredLifetime { get; set; }

    //
    // Summary:
    //     Gets the number of seconds remaining during which this address is valid.
    //
    // Returns:
    //     An System.Int64 value that specifies the number of seconds left for this address
    //     to remain assigned.
    //
    // Exceptions:
    //   T:System.PlatformNotSupportedException:
    //     This property is not valid on computers running operating systems earlier than
    //     Windows XP.
    public long? AddressValidLifetime { get; set; }

    //
    // Summary:
    //     Specifies the amount of time remaining on the Dynamic Host Configuration Protocol
    //     (DHCP) lease for this IP address.
    //
    // Returns:
    //     An System.Int64 value that contains the number of seconds remaining before the
    //     computer must release the System.Net.IPAddress instance.
    public long? DhcpLeaseLifetime { get; set; }

    //
    // Summary:
    //     Gets a value that indicates the state of the duplicate address detection algorithm.
    //
    // Returns:
    //     One of the System.Net.NetworkInformation.DuplicateAddressDetectionState values
    //     that indicates the progress of the algorithm in determining the uniqueness of
    //     this IP address.
    //
    // Exceptions:
    //   T:System.PlatformNotSupportedException:
    //     This property is not valid on computers running operating systems earlier than
    //     Windows XP.
    public DuplicateAddressDetectionState? DuplicateAddressDetectionState { get; set; }

    //
    // Summary:
    //     Gets a value that identifies the source of a Multicast Internet Protocol (IP)
    //     address prefix.
    //
    // Returns:
    //     One of the System.Net.NetworkInformation.PrefixOrigin values that identifies
    //     how the prefix information was obtained.
    //
    // Exceptions:
    //   T:System.PlatformNotSupportedException:
    //     This property is not valid on computers running operating systems earlier than
    //     Windows XP.
    public PrefixOrigin? PrefixOrigin { get; set; }

    //
    // Summary:
    //     Gets a value that identifies the source of a Multicast Internet Protocol (IP)
    //     address suffix.
    //
    // Returns:
    //     One of the System.Net.NetworkInformation.SuffixOrigin values that identifies
    //     how the suffix information was obtained.
    //
    // Exceptions:
    //   T:System.PlatformNotSupportedException:
    //     This property is not valid on computers running operating systems earlier than
    //     Windows XP.
    public SuffixOrigin? SuffixOrigin { get; set; }
}

public class JmwUnicastIpAddressInformation : JmwIpAddressInformation
{
    public JmwUnicastIpAddressInformation()
    {
    }

    public JmwUnicastIpAddressInformation(UnicastIPAddressInformation info)
    {
        this.Address = new JmwIpAddress(info.Address);
        this.IsDnsEligible = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.IsDnsEligible : null;
        this.IsTransient = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.IsTransient : null;

        this.AddressPreferredLifetime = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.AddressPreferredLifetime : null;
        this.AddressValidLifetime = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.AddressValidLifetime : null;
        this.DhcpLeaseLifetime = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.DhcpLeaseLifetime : null;
        this.DuplicateAddressDetectionState = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.DuplicateAddressDetectionState : null;
        this.PrefixOrigin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.PrefixOrigin : null;
        this.SuffixOrigin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.SuffixOrigin : null;
        this.IPv4Mask = new JmwIpAddress(info.IPv4Mask);
    }

    /// <summary>
    /// Gets the number of seconds remaining during which this address is the preferred address.
    /// </summary>
    public long? AddressPreferredLifetime { get; set; }

    /// <summary>
    /// Gets the number of seconds remaining during which this address is valid.
    /// </summary>
    public long? AddressValidLifetime { get; set; }

    /// <summary>
    /// Specifies the amount of time remaining on the Dynamic Host Configuration Protocol (DHCP) lease for this IP address.
    /// </summary>
    public long? DhcpLeaseLifetime { get; set; }

    /// <summary>
    /// Gets a value that indicates the state of the duplicate address detection algorithm.
    /// </summary>
    public DuplicateAddressDetectionState? DuplicateAddressDetectionState { get; set; }

    /// <summary>
    /// Gets a value that identifies the source of a unicast IP address prefix.
    /// </summary>
    public PrefixOrigin? PrefixOrigin { get; set; }

    /// <summary>
    /// Gets a value that identifies the source of a unicast IP address suffix.
    /// </summary>
    public SuffixOrigin? SuffixOrigin { get; set; }

    public JmwIpAddress? IPv4Mask { get; set; }
}

public class JmwIpAddressInformation
{
    public JmwIpAddressInformation()
    {
    }

    public JmwIpAddressInformation(IPAddressInformation info)
    {
        this.Address = new JmwIpAddress(info.Address);
        this.IsDnsEligible = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.IsDnsEligible : null;
        this.IsTransient = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? info.IsTransient : null;
    }

    public JmwIpAddress? Address { get; set; }

    public bool? IsDnsEligible { get; set; }

    public bool? IsTransient { get; set; }
}