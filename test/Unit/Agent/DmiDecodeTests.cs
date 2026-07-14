using JMW.Discovery.Agent.Collection.Local;

namespace JMW.Discovery.UnitTests.Agent;

public sealed class DmiDecodeTests
{
    // Representative `dmidecode -q` output: real section headers, a multi-line
    // "Characteristics" block (bare key + non-colon bullets), OEM placeholder values,
    // an unpopulated CPU socket, and an empty memory slot.
    private const string Sample =
        """
        BIOS Information
        	Vendor: American Megatrends Inc.
        	Version: 1.2.3
        	Release Date: 03/12/2020
        	Characteristics:
        		PCI is supported
        		BIOS is upgradeable

        System Information
        	Manufacturer: Dell Inc.
        	Product Name: PowerEdge R740
        	Serial Number: ABCD123
        	UUID: 4c4c4544-0042-4210-8043-b9c04f313233

        Chassis Information
        	Manufacturer: Dell Inc.
        	Type: Rack Mount Chassis
        	Serial Number: CH-9981
        	Asset Tag: DATACENTER-07

        Base Board Information
        	Manufacturer: Dell Inc.
        	Product Name: 0X0R1H
        	Serial Number: .ABCD123.CN

        Processor Information
        	Socket Designation: CPU1
        	Status: Populated, Enabled
        	Manufacturer: Intel(R) Corporation
        	Family: Xeon
        	Version: Intel(R) Xeon(R) Gold 6230 CPU @ 2.10GHz
        	Serial Number: Not Specified
        	Core Count: 20
        	Thread Count: 40
        	Max Speed: 3900 MHz

        Processor Information
        	Socket Designation: CPU2
        	Status: Unpopulated
        	Manufacturer: Not Specified

        Memory Device
        	Locator: DIMM_A1
        	Size: 16 GB
        	Type: DDR4
        	Form Factor: DIMM
        	Speed: 2933 MT/s
        	Manufacturer: Hynix
        	Part Number: HMA82GR7CJR8N
        	Serial Number: 12AB34CD

        Memory Device
        	Locator: DIMM_A2
        	Size: No Module Installed
        """;

    [Fact]
    public void Parse_ExtractsAllSectionsInOrder()
    {
        IReadOnlyList<DmiDecode.Section> sections = DmiDecode.Parse(Sample);

        Assert.Equal(
            [
                "BIOS Information", "System Information", "Chassis Information",
                "Base Board Information", "Processor Information", "Processor Information",
                "Memory Device", "Memory Device",
            ],
            sections.Select(s => s.Name)
        );
    }

    [Fact]
    public void Parse_CapturesScalarFieldsAndSkipsMultiLineBlocks()
    {
        DmiDecode.Section? bios = DmiDecode.Find(DmiDecode.Parse(Sample), "BIOS Information");

        Assert.NotNull(bios);
        Assert.Equal("American Megatrends Inc.", DmiDecode.Get(bios, "Vendor"));
        Assert.Equal("1.2.3", DmiDecode.Get(bios, "Version"));
        Assert.Equal("03/12/2020", DmiDecode.Get(bios, "Release Date"));
        // "Characteristics:" is a bare key with a multi-line value → not captured, and its
        // non-colon bullet lines ("PCI is supported") never become fields.
        Assert.Null(DmiDecode.Get(bios, "Characteristics"));
        Assert.False(bios.Fields.ContainsKey("PCI is supported"));
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndGetReturnsNullForMissing()
    {
        IReadOnlyList<DmiDecode.Section> sections = DmiDecode.Parse(Sample);

        Assert.NotNull(DmiDecode.Find(sections, "chassis information"));
        Assert.Null(DmiDecode.Find(sections, "Cache Information"));
        Assert.Null(DmiDecode.Get(DmiDecode.Find(sections, "System Information"), "Nonexistent"));
    }

    [Fact]
    public void Parse_HandlesCrlfLineAndBlockSeparators()
    {
        string crlf = Sample.Replace("\n", "\r\n");
        IReadOnlyList<DmiDecode.Section> sections = DmiDecode.Parse(crlf);

        DmiDecode.Section? system = DmiDecode.Find(sections, "System Information");
        Assert.NotNull(system);
        // No stray trailing \r on the value.
        Assert.Equal("PowerEdge R740", DmiDecode.Get(system, "Product Name"));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Not Specified", null)]
    [InlineData("To Be Filled By O.E.M.", null)]
    [InlineData("Default string", null)]
    [InlineData("System Serial Number", null)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("not specified", null)] // case-insensitive placeholder match
    [InlineData("  Dell Inc.  ", "Dell Inc.")] // trimmed, real value survives
    [InlineData("ABCD123", "ABCD123")]
    public void Clean_ScrubsPlaceholdersAndTrims(string input, string? expected) =>
        Assert.Equal(expected, DmiDecode.Clean(input));

    [Fact]
    public void Clean_NullInputReturnsNull() => Assert.Null(DmiDecode.Clean(null));

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n ")]
    public void Parse_EmptyOrBlankReturnsNoSections(string input) =>
        Assert.Empty(DmiDecode.Parse(input));
}