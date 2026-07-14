using JMW.Discovery.Agent.Collection.Network;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Tests the UPnP device-description parser against spec-shaped documents: it reads the root
/// device's manufacturer/model/serial/friendlyName, combines modelName+modelNumber sensibly,
/// tolerates the UPnP namespace, ignores sub-devices in a deviceList, and rejects junk.
/// </summary>
public sealed class UpnpDeviceDescriptionTests
{
    [Fact]
    public void Parse_ExtractsRootDeviceIdentity()
    {
        const string xml =
            """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <specVersion><major>1</major><minor>0</minor></specVersion>
              <device>
                <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
                <friendlyName>Living Room Router</friendlyName>
                <manufacturer>Netgear</manufacturer>
                <modelName>R7000</modelName>
                <modelNumber>V1.0.9</modelNumber>
                <serialNumber>4C3FB2A1</serialNumber>
                <deviceList>
                  <device>
                    <manufacturer>ShouldBeIgnored</manufacturer>
                    <modelName>SubDevice</modelName>
                  </device>
                </deviceList>
              </device>
            </root>
            """;

        HttpDeepFields? f = UpnpDeviceDescription.Parse(xml);

        Assert.NotNull(f);
        Assert.Equal("Netgear", f.Vendor);
        Assert.Equal("R7000 V1.0.9", f.Model); // modelName + modelNumber
        Assert.Equal("4C3FB2A1", f.Serial);
        Assert.Equal("Living Room Router", f.FriendlyName);
        Assert.Null(f.Firmware); // UPnP has no firmware element
    }

    [Fact]
    public void Parse_DoesNotDuplicateModelNumberAlreadyInName()
    {
        const string xml =
            """
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device>
              <manufacturer>Acme</manufacturer>
              <modelName>Widget R7000</modelName>
              <modelNumber>R7000</modelNumber>
            </device></root>
            """;

        Assert.Equal("Widget R7000", UpnpDeviceDescription.Parse(xml)?.Model);
    }

    [Fact]
    public void Parse_ReturnsNullForNonDeviceXml()
    {
        Assert.Null(UpnpDeviceDescription.Parse("<html><body>not upnp</body></html>"));
    }

    [Fact]
    public void Parse_ReturnsNullForMalformedXml()
    {
        Assert.Null(UpnpDeviceDescription.Parse("<root><device><manufacturer>oops"));
    }

    [Fact]
    public void Parse_ReturnsNullWhenNoIdentityFields()
    {
        const string xml = "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><deviceType>x</deviceType></device></root>";
        Assert.Null(UpnpDeviceDescription.Parse(xml));
    }

    [Theory]
    [InlineData("<a href=\"/rootDesc.xml\">desc</a>", "http://10.0.0.1/rootDesc.xml")]
    [InlineData("<link rel=x href='/upnp/DeviceDescription.xml'>", "http://10.0.0.1/upnp/DeviceDescription.xml")]
    [InlineData("location=http://10.0.0.1:49152/gatedesc.xml", "http://10.0.0.1:49152/gatedesc.xml")]
    [InlineData("<html>nothing here</html>", null)]
    public void FindUpnpDescriptionUrl_FindsHintedReferenceOrNull(string body, string? expected)
    {
        Uri baseUri = new("http://10.0.0.1/");
        Uri? found = HttpBannerScanner.FindUpnpDescriptionUrl(baseUri, body);
        Assert.Equal(expected, found?.ToString());
    }
}