using JMW.Discovery.Agent.Collection.Network;
using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Unit tests for <see cref="NetworkDiscoveryCollector.PromoteAttributes" /> — the
/// mapping of raw scanner <c>Attr[key]</c> values to typed discovered-* fact paths.
/// Guards the priority-ordered Vendor/Model/Firmware candidate lists.
/// </summary>
public sealed class NetworkDiscoveryPromotionTests
{
    private const string Dev = "dev1";
    private const string Ip = "192.168.1.5";

    private static string? Promoted(Dictionary<string, string> attrs, string factPath)
    {
        List<Fact> facts = [];
        NetworkDiscoveryCollector.PromoteAttributes(facts, Dev, Ip, attrs);
        string id = factPath.Replace("Device[]", $"Device[{Dev}]").Replace("Discovered[]", $"Discovered[{Ip}]");
        return facts.FirstOrDefault(fact => fact.Id == id) is { } f ? f.Value.AsString() : null;
    }

    private static Fact? PromotedFact(Dictionary<string, string> attrs, string factPath)
    {
        List<Fact> facts = [];
        NetworkDiscoveryCollector.PromoteAttributes(facts, Dev, Ip, attrs);
        string id = factPath.Replace("Device[]", $"Device[{Dev}]").Replace("Discovered[]", $"Discovered[{Ip}]");
        return facts.FirstOrDefault(f => f.Id == id);
    }

    [Fact]
    public void AirPlayVersion_PromotesToFirmware()
    {
        // Regression for the T2-1 near-miss: airplay.version was parsed and emitted as a
        // raw Attr but omitted from the Firmware candidate list, so it never became a
        // queryable Firmware fact.
        Dictionary<string, string> attrs = new()
        {
            ["airplay.model"] = "AppleTV6,2",
            ["airplay.version"] = "16.5",
        };

        Assert.Equal("16.5", Promoted(attrs, FactPaths.DiscoveredFirmware));
        Assert.Equal("AppleTV6,2", Promoted(attrs, FactPaths.DiscoveredModel));
    }

    [Fact]
    public void RokuModelNumber_PromotesToModel_OnlyWhenModelNameAbsent()
    {
        // roku.model (friendly name) wins when present; roku.model_number (SKU) is only a
        // last-resort fallback so a Roku missing the friendly name still shows something.
        Assert.Equal(
            "4670X",
            Promoted(
                new Dictionary<string, string>
                {
                    ["roku.model_number"] = "4670X",
                },
                FactPaths.DiscoveredModel
            )
        );
        Assert.Equal(
            "Roku Ultra",
            Promoted(
                new Dictionary<string, string>
                {
                    ["roku.model"] = "Roku Ultra",
                    ["roku.model_number"] = "4670X",
                },
                FactPaths.DiscoveredModel
            )
        );
    }

    [Fact]
    public void ModelPriority_OnvifWinsOverLaterSources()
    {
        // The candidate list is priority-ordered: the first present key wins.
        Dictionary<string, string> attrs = new()
        {
            ["onvif.model"] = "CamPro-9000",
            ["upnp.model"] = "GenericUPnP",
            ["airplay.model"] = "AppleTV6,2",
        };

        Assert.Equal("CamPro-9000", Promoted(attrs, FactPaths.DiscoveredModel));
    }

    [Fact]
    public void NoMatchingAttributes_EmitsNothing()
    {
        Dictionary<string, string> attrs = new()
        {
            ["unrelated.key"] = "x",
        };

        Assert.Null(Promoted(attrs, FactPaths.DiscoveredFirmware));
        Assert.Null(Promoted(attrs, FactPaths.DiscoveredModel));
        Assert.Null(Promoted(attrs, FactPaths.DiscoveredVendor));
    }

    [Fact]
    public void SnmpPrinterSerial_PromotesToTypedFact()
    {
        Assert.Equal(
            "CNF8G353WR",
            Promoted(new Dictionary<string, string> { ["snmp.printer_serial"] = "CNF8G353WR" }, FactPaths.DiscoveredSnmpSerial)
        );
    }

    [Fact]
    public void IppFirmware_PromotesToFirmware()
    {
        // Vendor-neutral IPP firmware feeds the shared Firmware fact (ranks above the generic
        // http.identity guess, below device-specific protocols).
        Assert.Equal(
            "20130415",
            Promoted(new Dictionary<string, string> { ["ipp.firmware"] = "20130415" }, FactPaths.DiscoveredFirmware)
        );
    }

    [Fact]
    public void HttpIdentity_PromotesToTypedIdentityFacts()
    {
        Dictionary<string, string> attrs = new()
        {
            ["http.identity.vendor"] = "Ubiquiti",
            ["http.identity.model"] = "UniFi AP",
            ["http.identity.firmware"] = "6.2.44",
            ["http.identity.type"] = "Access Point",
            ["http.identity.os"] = "Linux",
            ["http.identity.source"] = "vendor:server,model:favicon",
        };

        Assert.Equal("Ubiquiti", Promoted(attrs, FactPaths.DiscoveredVendor));
        Assert.Equal("UniFi AP", Promoted(attrs, FactPaths.DiscoveredModel));
        Assert.Equal("6.2.44", Promoted(attrs, FactPaths.DiscoveredFirmware));
        Assert.Equal("Access Point", Promoted(attrs, FactPaths.DiscoveredDeviceType));
        Assert.Equal("Linux", Promoted(attrs, FactPaths.DiscoveredOs));
        Assert.Equal("vendor:server,model:favicon", Promoted(attrs, FactPaths.DiscoveredHttpIdentitySource));
    }

    [Fact]
    public void DeviceSpecificProtocol_OutranksHttpIdentity()
    {
        // http.identity.* is ranked last, so a device-specific protocol signal wins the shared fact.
        Dictionary<string, string> attrs = new()
        {
            ["onvif.manufacturer"] = "Hikvision",
            ["http.identity.vendor"] = "GenericHttpGuess",
        };

        Assert.Equal("Hikvision", Promoted(attrs, FactPaths.DiscoveredVendor));
    }

    [Fact]
    public void HttpConfidence_PromotesToTypedDoubleFact()
    {
        Dictionary<string, string> attrs = new() { ["http.identity.confidence"] = "0.85" };

        List<Fact> facts = [];
        NetworkDiscoveryCollector.PromoteAttributes(facts, Dev, Ip, attrs);
        string id = FactPaths
            .DiscoveredHttpConfidence.Replace("Device[]", $"Device[{Dev}]")
            .Replace("Discovered[]", $"Discovered[{Ip}]");
        Assert.Equal(0.85, facts.First(f => f.Id == id).Value.AsDouble());
    }

    [Fact]
    public void FaviconHashes_PromoteToTypedFacts()
    {
        Dictionary<string, string> attrs = new()
        {
            ["http.favicon.md5"] = "e6899eaaf06fd702f3ed3f988eb19362",
            ["http.favicon.mmh3"] = "-156908512",
        };

        Assert.Equal("e6899eaaf06fd702f3ed3f988eb19362", Promoted(attrs, FactPaths.DiscoveredFaviconMd5));

        // mmh3 is a typed Long fact (signed 32-bit), not a string.
        List<Fact> facts = [];
        NetworkDiscoveryCollector.PromoteAttributes(facts, Dev, Ip, attrs);
        string id = FactPaths
            .DiscoveredFaviconMmh3.Replace("Device[]", $"Device[{Dev}]")
            .Replace("Discovered[]", $"Discovered[{Ip}]");
        Assert.Equal(-156908512L, facts.First(f => f.Id == id).Value.AsLong());
    }

    // ── Source (provenance) stamping ───────────────────────────────────────────
    // Every attribute key is written by exactly one scanner, so PromoteAttributes infers
    // FactSource from the key without needing it threaded through every call site. These
    // exercise each Emit* helper's stamping, plus the case that motivated an exact-key (not
    // prefix) lookup: "snmp.*" is written by two different scanners for different keys.

    [Fact]
    public void EmitAttrAs_StampsSourceFromKey()
    {
        Fact? fact = PromotedFact(new Dictionary<string, string> { ["ipp.location"] = "Room 204" },
            FactPaths.DiscoveredIppLocation);
        Assert.Equal(FactSource.Ipp, fact?.Source);
    }

    [Fact]
    public void EmitAttrAsLong_StampsSourceFromKey()
    {
        Fact? fact = PromotedFact(new Dictionary<string, string> { ["http.status"] = "200" },
            FactPaths.DiscoveredHttpStatus);
        Assert.Equal(FactSource.HttpBanner, fact?.Source);
    }

    [Fact]
    public void EmitAttrAsBool_StampsSourceFromKey()
    {
        Fact? fact = PromotedFact(new Dictionary<string, string> { ["onvif.auth_required"] = "true" },
            FactPaths.DiscoveredOnvifAuthRequired);
        Assert.Equal(FactSource.Onvif, fact?.Source);
    }

    [Fact]
    public void EmitAttrList_StampsSourceOnEveryItem()
    {
        List<Fact> facts = [];
        NetworkDiscoveryCollector.PromoteAttributes(
            facts,
            Dev,
            Ip,
            new Dictionary<string, string> { ["mdns.services"] = "_http._tcp,_ssh._tcp" }
        );

        List<Fact> serviceFacts = facts.Where(f => f.AttributePath == FactPaths.DiscoveredServiceName).ToList();
        Assert.Equal(2, serviceFacts.Count);
        Assert.All(serviceFacts, f => Assert.Equal(FactSource.Mdns, f.Source));
    }

    [Fact]
    public void EmitFirstNonNull_StampsSourceOfWhicheverCandidateWon()
    {
        // Onvif wins (first candidate present) -> Onvif source, not the HttpBanner fallback.
        Fact? viaOnvif = PromotedFact(
            new Dictionary<string, string>
            {
                ["onvif.manufacturer"] = "Hikvision",
                ["http.identity.vendor"] = "GenericHttpGuess",
            },
            FactPaths.DiscoveredVendor
        );
        Assert.Equal(FactSource.Onvif, viaOnvif?.Source);

        // Only the HTTP fallback present -> HttpBanner source.
        Fact? viaHttp = PromotedFact(
            new Dictionary<string, string> { ["http.identity.vendor"] = "GenericHttpGuess" },
            FactPaths.DiscoveredVendor
        );
        Assert.Equal(FactSource.HttpBanner, viaHttp?.Source);
    }

    [Fact]
    public void AmbiguousSnmpPrefix_ResolvesToTheCorrectScannerPerExactKey()
    {
        // Both scanners write "snmp.*" keys, but different ones -- an exact-key lookup (not a
        // "snmp." prefix) is required to tell them apart.
        Fact? printerSerial = PromotedFact(
            new Dictionary<string, string> { ["snmp.printer_serial"] = "CNF8G353WR" },
            FactPaths.DiscoveredSnmpSerial
        );
        Assert.Equal(FactSource.SnmpPrinter, printerSerial?.Source);

        Fact? sysName = PromotedFact(
            new Dictionary<string, string> { ["snmp.sysname"] = "switch-01" },
            FactPaths.DiscoveredHostname
        );
        Assert.Equal(FactSource.SnmpBroadcast, sysName?.Source);
    }

    [Fact]
    public void EveryKnownAttributeKey_ResolvesToASpecificSource_NotTheGenericFallback()
    {
        // Every key PromoteAttributes actually reads has a specific entry in KeyToSource
        // (verified against each scanner's source when the mapping was written); none should
        // silently land on the NetworkDiscovery catch-all, which is reserved for the
        // MAC/Hostname/Sources facts merged across scanners before creation.
        Dictionary<string, string> attrs = new()
        {
            ["airplay.features"] = "x",
            ["airplay.model"] = "x",
            ["airplay.plist_format"] = "x",
            ["airplay.version"] = "x",
            ["bacnet.instance"] = "1",
            ["bacnet.vendor_id"] = "1",
            ["coap.resources"] = "x",
            ["coap.types"] = "x",
            ["eureka.cast_version"] = "x",
            ["eureka.model"] = "x",
            ["eureka.ssid"] = "x",
            ["eureka.version"] = "x",
            ["http.favicon.md5"] = "x",
            ["http.favicon.mmh3"] = "1",
            ["http.identity.confidence"] = "0.5",
            ["http.identity.firmware"] = "x",
            ["http.identity.model"] = "x",
            ["http.identity.name"] = "x",
            ["http.identity.os"] = "x",
            ["http.identity.serial"] = "x",
            ["http.identity.source"] = "x",
            ["http.identity.type"] = "x",
            ["http.identity.vendor"] = "x",
            ["http.server"] = "x",
            ["http.status"] = "200",
            ["http.title"] = "x",
            ["http.url"] = "x",
            ["hue.api_version"] = "x",
            ["hue.bridge_id"] = "x",
            ["hue.model"] = "x",
            ["hue.version"] = "x",
            ["ipp.firmware"] = "x",
            ["ipp.location"] = "x",
            ["ipp.model"] = "x",
            ["ldap.naming_context"] = "x",
            ["ldap.server_name"] = "x",
            ["mdns.services"] = "x",
            ["modbus.port"] = "1",
            ["modbus.unit_id"] = "1",
            ["mqtt.auth_required"] = "true",
            ["mqtt.port"] = "1",
            ["mqtt.return_code"] = "x",
            ["nbns.authoritative"] = "true",
            ["nbns.broadcast"] = "true",
            ["nbns.name_details"] = "x|1|d|Broadcast|false|false|false|false|false",
            ["nbns.names"] = "x",
            ["nbns.op_code"] = "x",
            ["nbns.recursion_available"] = "true",
            ["nbns.recursion_desired"] = "true",
            ["nbns.result_code"] = "x",
            ["nbns.truncated"] = "true",
            ["onvif.auth_required"] = "true",
            ["onvif.firmware"] = "x",
            ["onvif.hardware_id"] = "x",
            ["onvif.manufacturer"] = "x",
            ["onvif.model"] = "x",
            ["onvif.serial"] = "x",
            ["roku.model"] = "x",
            ["roku.model_number"] = "x",
            ["roku.serial"] = "x",
            ["roku.version"] = "x",
            ["rtsp.content_type"] = "x",
            ["rtsp.methods"] = "x",
            ["rtsp.port"] = "1",
            ["rtsp.server"] = "x",
            ["smb2.dialect"] = "x",
            ["snmp.printer_serial"] = "x",
            ["snmp.sysname"] = "x",
            ["ssdp.server"] = "x",
            ["ssdp.st"] = "x",
            ["ssh.banner"] = "x",
            ["ssh.host-key-fp"] = "x",
            ["tls.cn"] = "x",
            ["tls.subject"] = "x",
            ["tls.issuer"] = "x",
            ["tls.serial"] = "x",
            ["upnp.device_type"] = "x",
            ["upnp.manufacturer"] = "x",
            ["upnp.model"] = "x",
            ["upnp.presentation_url"] = "x",
            ["wsd.address"] = "x",
            ["wsd.metadata_version"] = "x",
            ["wsd.types"] = "x",
        };

        List<Fact> facts = [];
        NetworkDiscoveryCollector.PromoteAttributes(facts, Dev, Ip, attrs);

        Assert.NotEmpty(facts);
        Assert.DoesNotContain(facts, f => f.Source is FactSource.NetworkDiscovery or FactSource.Unknown);
    }
}