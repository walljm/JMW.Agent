using JMW.Discovery.Agent.Collection.Network;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Tests the verified printer identity parsers (HP LEDM XML, Brother dt/dd HTML, Samsung SyncThru
/// JSON, Epson EWS "Advanced" pages) against representative structures — HP and Epson fixtures are
/// trimmed from real device responses (OfficeJet Pro 6970, LaserJet M209dw, SC-P900). Vendors
/// without a stable HTTP format (Canon/Lexmark/Xerox/Ricoh/Kyocera/Konica) stay best-effort/model-only
/// here — full identity routes through SNMP/IPP for those.
/// </summary>
public sealed class PrinterFollowUpsTests
{
    [Fact]
    public void ParseHpLedm_ExtractsModelSerialFirmware()
    {
        const string xml =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <prdcfgdyn:ProductConfigDyn
                xmlns:dd="http://www.hp.com/schemas/imaging/con/dictionaries/1.0/"
                xmlns:prdcfgdyn="http://www.hp.com/schemas/imaging/con/ledm/productconfigdyn/2007/11/05">
              <dd:Version><dd:Revision>SVN-IPG-LEDM.119</dd:Revision></dd:Version>
              <prdcfgdyn:ProductInformation>
                <dd:MakeAndModel>HP LaserJet 400 MFP M425dw</dd:MakeAndModel>
                <dd:SerialNumber>CNF8G353WR</dd:SerialNumber>
                <dd:ProductNumber>CF288A</dd:ProductNumber>
                <dd:Version><dd:Revision>20130415</dd:Revision><dd:Date>2013-04-15</dd:Date></dd:Version>
              </prdcfgdyn:ProductInformation>
            </prdcfgdyn:ProductConfigDyn>
            """;

        HttpDeepFields? f = PrinterFollowUps.ParseHpLedm(xml);

        Assert.NotNull(f);
        Assert.Equal("HP", f.Vendor);
        Assert.Equal("HP LaserJet 400 MFP M425dw", f.Model);
        Assert.Equal("CNF8G353WR", f.Serial);
        Assert.Equal("20130415", f.Firmware); // ProductInformation/Version/Revision, NOT the schema revision
    }

    [Fact]
    public void ParseBrother_ExtractsSerialAndMainFirmware()
    {
        const string html =
            """
            <html><body><dl>
              <dt>Node Name</dt><dd>BRN30055C123456</dd>
              <dt>Serial No.</dt><dd>U63812J4N123456</dd>
              <dt>Main Firmware Version</dt><dd>1.42</dd>
              <dt>Sub1 Firmware Version</dt><dd>1.09</dd>
            </dl></body></html>
            """;

        HttpDeepFields? f = PrinterFollowUps.ParseBrother(html);

        Assert.NotNull(f);
        Assert.Equal("U63812J4N123456", f.Serial);
        Assert.Equal("1.42", f.Firmware);
        Assert.Null(f.Model); // model isn't reliable on this page
    }

    [Fact]
    public void ParseSyncThru_ExtractsModelAndSerial()
    {
        const string json =
            """
            { "status": { "hrDeviceStatus": 2 },
              "identity": { "model_name": "M2070 Series", "serial_num": "Z6DGBJHF400123X",
                            "host_name": "SEC30CDA7", "mac_addr": "30:cd:a7:00:00:00" } }
            """;

        HttpDeepFields? f = PrinterFollowUps.ParseSyncThru(json);

        Assert.NotNull(f);
        Assert.Equal("M2070 Series", f.Model);
        Assert.Equal("Z6DGBJHF400123X", f.Serial);
        Assert.Null(f.Firmware); // not in the JSON
    }

    [Fact]
    public void ParseCanon_ExtractsModelAndOpportunisticSerial()
    {
        const string html =
            """
            <html><body>
            <span id="deviceName">iR-ADV C5235 - JWC04988  / iR-ADV C5235 /  Vietnam</span>
            </body></html>
            """;

        HttpDeepFields? f = PrinterFollowUps.ParseCanon(html);

        Assert.NotNull(f);
        Assert.Equal("Canon", f.Vendor);
        Assert.Equal("iR-ADV C5235", f.Model); // middle segment
        Assert.Equal("JWC04988", f.Serial); // after " - " in the first segment
    }

    [Fact]
    public void ParseCanon_ModelOnlyWhenNoSerialSegment()
    {
        HttpDeepFields? f = PrinterFollowUps.ParseCanon("<span id=\"deviceName\">MF445dw</span>");
        Assert.NotNull(f);
        Assert.Equal("MF445dw", f.Model);
        Assert.Null(f.Serial);
    }

    [Fact]
    public void CanonAndEpson_ReturnNullOnJunk()
    {
        Assert.Null(PrinterFollowUps.ParseCanon("<html>no deviceName span</html>"));
        Assert.Null(PrinterFollowUps.ParseEpsonModelName("<html>no model_name h1</html>"));
    }

    [Theory]
    [InlineData("brother", "Brother", null, true)]
    [InlineData("syncthru", "Samsung", "M2070", true)]
    [InlineData("canon", "Canon", null, true)]
    [InlineData("canon", "HP", "LaserJet", false)]
    public void Descriptor_AppliesMatchesVendorOrModel(string source, string? vendor, string? model, bool expected)
    {
        PrinterFollowUps.Descriptor d = PrinterFollowUps.All.Single(x => x.Source == source);
        HttpIdentity id = new(vendor, model, null, null, null, null, null, 0.75, "");
        Assert.Equal(expected, d.Applies(id));
    }

    [Theory]
    [InlineData("HP", "HP LaserJet 400", true)]
    [InlineData("Brother", "HL-L2350DW", false)]
    [InlineData(null, "HP OfficeJet Pro", true)] // matches on model when vendor absent
    public void IsHp_MatchesVendorOrModel(string? vendor, string? model, bool expected)
    {
        HttpIdentity id = new(vendor, model, null, null, null, null, null, 0.75, "");
        Assert.Equal(expected, PrinterFollowUps.IsHp(id));
    }

    [Theory]
    [InlineData("EPSON", "WF-3720", true)]
    [InlineData("HP", "LaserJet", false)]
    public void IsEpson_MatchesVendorOrModel(string? vendor, string? model, bool expected)
    {
        HttpIdentity id = new(vendor, model, null, null, null, null, null, 0.75, "");
        Assert.Equal(expected, PrinterFollowUps.IsEpson(id));
    }

    [Fact]
    public void IsHp_FallsBackToRawTitleWhenRecogResolvesGenericServerSoftware()
    {
        // Confirmed live against an HP LaserJet M209dw: its Server header is
        // "Virata-EmWeb/R6_2_1" (HP licenses Allegro's EmWeb as its EWS backend), which Recog
        // resolves to vendor=null/model="EmWeb" — no "hp"/"laserjet" substring anywhere in the
        // resolved identity, even though the page title plainly says "HP LaserJet M209dw".
        HttpIdentity shallow = new(null, "EmWeb", "R6_2_1", null, null, null, null, 0.75, "vendor:server,model:server");
        HttpIdentitySignals signals = new(
            Server: "Virata-EmWeb/R6_2_1",
            Title: "HP LaserJet M209dw   192.168.1.233",
            WwwAuthenticate: null,
            FaviconMd5: null
        );

        Assert.False(PrinterFollowUps.IsHp(shallow)); // without signals, the gap reproduces
        Assert.True(PrinterFollowUps.IsHp(shallow, signals)); // with signals, the title saves it
    }

    [Fact]
    public void IsEpson_FallsBackToRawServerHeader()
    {
        HttpIdentity? shallow = null; // Recog found nothing at all
        HttpIdentitySignals signals = new(
            Server: "EPSON_Linux, UPnP/1.0, Epson, UPnP, SDK/1.0",
            Title: null,
            WwwAuthenticate: null,
            FaviconMd5: null
        );

        Assert.False(PrinterFollowUps.IsEpson(shallow));
        Assert.True(PrinterFollowUps.IsEpson(shallow, signals));
    }

    [Fact]
    public void Parsers_ReturnNullOnJunk()
    {
        Assert.Null(PrinterFollowUps.ParseHpLedm("<html>not ledm</html>"));
        Assert.Null(PrinterFollowUps.ParseBrother("<html>no definition list</html>"));
        Assert.Null(PrinterFollowUps.ParseSyncThru("not json"));
        Assert.Null(PrinterFollowUps.ParseSyncThru("{\"other\":1}"));
    }

    // ── HP LEDM: product number / status / consumables ─────────────────────────

    [Fact]
    public void ParseHpProductNumber_ExtractsSku()
    {
        const string xml =
            """
            <prdcfgdyn:ProductConfigDyn xmlns:dd="http://www.hp.com/schemas/imaging/con/dictionaries/1.0/"
                xmlns:prdcfgdyn="http://www.hp.com/schemas/imaging/con/ledm/productconfigdyn/2007/11/05">
              <prdcfgdyn:ProductInformation>
                <dd:MakeAndModel>OfficeJet Pro 6978 All-in-One</dd:MakeAndModel>
                <dd:ProductNumber>T0F29A</dd:ProductNumber>
              </prdcfgdyn:ProductInformation>
            </prdcfgdyn:ProductConfigDyn>
            """;

        Assert.Equal("T0F29A", PrinterFollowUps.ParseHpProductNumber(xml));
        Assert.Null(PrinterFollowUps.ParseHpProductNumber("<html>not ledm</html>"));
    }

    [Fact]
    public void ParseHpStatus_JoinsCategoriesAndFiltersInfoAlerts()
    {
        // Trimmed from a real OfficeJet Pro ProductStatusDyn.xml response.
        const string xml =
            """
            <psdyn:ProductStatusDyn xmlns:dd="http://www.hp.com/schemas/imaging/con/dictionaries/1.0/"
                xmlns:pscat="http://www.hp.com/schemas/imaging/con/ledm/productstatuscategories/2007/10/31"
                xmlns:ad="http://www.hp.com/schemas/imaging/con/ledm/alertdetails/2007/10/31"
                xmlns:psdyn="http://www.hp.com/schemas/imaging/con/ledm/productstatusdyn/2007/10/31">
              <psdyn:Status><pscat:StatusCategory>cartridgeCounterfeitQuestion</pscat:StatusCategory></psdyn:Status>
              <psdyn:Status><pscat:StatusCategory>inPowerSave</pscat:StatusCategory></psdyn:Status>
              <psdyn:AlertTable>
                <psdyn:Alert>
                  <ad:ProductStatusAlertID>cartridgeCounterfeitQuestion</ad:ProductStatusAlertID>
                  <ad:Severity>Warning</ad:Severity>
                </psdyn:Alert>
                <psdyn:Alert>
                  <ad:ProductStatusAlertID>genuineHP</ad:ProductStatusAlertID>
                  <ad:Severity>Info</ad:Severity>
                </psdyn:Alert>
              </psdyn:AlertTable>
            </psdyn:ProductStatusDyn>
            """;

        (string? status, string? alerts) = PrinterFollowUps.ParseHpStatus(xml);

        Assert.Equal("cartridgeCounterfeitQuestion|inPowerSave", status);
        Assert.Equal("Warning:cartridgeCounterfeitQuestion", alerts); // Info severity filtered out
    }

    [Fact]
    public void ParseHpStatus_NoAlerts_ReturnsNullAlerts()
    {
        const string xml =
            """
            <psdyn:ProductStatusDyn xmlns:pscat="http://www.hp.com/schemas/imaging/con/ledm/productstatuscategories/2007/10/31"
                xmlns:psdyn="http://www.hp.com/schemas/imaging/con/ledm/productstatusdyn/2007/10/31">
              <psdyn:Status><pscat:StatusCategory>ready</pscat:StatusCategory></psdyn:Status>
            </psdyn:ProductStatusDyn>
            """;

        (string? status, string? alerts) = PrinterFollowUps.ParseHpStatus(xml);

        Assert.Equal("ready", status);
        Assert.Null(alerts);
    }

    [Fact]
    public void ParseHpConsumables_PrefersPercentOverQualitativeAndSkipsPrinthead()
    {
        const string xml =
            """
            <ccdyn:ConsumableConfigDyn xmlns:dd="http://www.hp.com/schemas/imaging/con/dictionaries/1.0/"
                xmlns:ccdyn="http://www.hp.com/schemas/imaging/con/ledm/consumableconfigdyn/2007/11/19">
              <ccdyn:ConsumableInfo>
                <dd:ConsumableTypeEnum>printhead</dd:ConsumableTypeEnum>
                <dd:ConsumableLabelCode>CMYK</dd:ConsumableLabelCode>
              </ccdyn:ConsumableInfo>
              <ccdyn:ConsumableInfo>
                <dd:ConsumableTypeEnum>toner</dd:ConsumableTypeEnum>
                <dd:ConsumableLabelCode>K</dd:ConsumableLabelCode>
                <dd:ConsumablePercentageLevelRemaining>90</dd:ConsumablePercentageLevelRemaining>
              </ccdyn:ConsumableInfo>
              <ccdyn:ConsumableInfo>
                <dd:ConsumableTypeEnum>ink</dd:ConsumableTypeEnum>
                <dd:ConsumableLabelCode>M</dd:ConsumableLabelCode>
                <dd:ConsumableLifeState><dd:MeasuredQuantityState>veryLow</dd:MeasuredQuantityState></dd:ConsumableLifeState>
              </ccdyn:ConsumableInfo>
            </ccdyn:ConsumableConfigDyn>
            """;

        string? consumables = PrinterFollowUps.ParseHpConsumables(xml);

        Assert.Equal("K:90%|M:veryLow", consumables); // printhead (CMYK) skipped
    }

    [Fact]
    public void ParseHpConsumables_NullOnJunk() => Assert.Null(PrinterFollowUps.ParseHpConsumables("<html>nope</html>"));

    // ── Epson EWS "Advanced" pages ──────────────────────────────────────────────

    [Fact]
    public void ParseEpsonModelName_ExtractsFromH1()
    {
        const string html = """<html><body><h1 id="model_name">SC-P900 Series</h1></body></html>""";
        Assert.Equal("SC-P900 Series", PrinterFollowUps.ParseEpsonModelName(html));
    }

    [Fact]
    public void ParseEpsonPrinterInfo_ExtractsSerialFirmwareStatusAndInkTanks()
    {
        // Trimmed shape from a real SC-P900 INFO_PRTINFO/TOP response.
        const string html =
            """
            <fieldset class="group"><legend>Printer Status</legend><ul class="values"><li class="value clearfix">
            <div class="preserve-white-space">Available.</div></li></ul></fieldset>
            <ul class="inksection">
            <li class='tank'><div class='tank'><img class='color' src='../../IMAGE/Ink_K.PNG' height='24'></div><div class='clrname'>MK</div></li>
            <li class='tank'><div class='tank'><img class='color' src='../../IMAGE/Ink_GY.PNG' height='1'>
            <img class='inkst' src='../../IMAGE/Icn_low.PNG' height='22' width='22'></div><div class='clrname'>LGY</div></li>
            <li class='tank'><div class='tank'><img class='color' src='../../IMAGE/Ink_MBOX.PNG' height='45'></div>
            <div class='mbicn'><img src='../../IMAGE/Icn_Mb.PNG' height='18' width='18'></div></li>
            <dl class="values"><dt class="key"><span class="key">Firmware&nbsp;:</span></dt>
            <dd class="value clearfix"><div class="preserve-white-space">04.55.KI03P9</div></dd>
            <dt class="key"><span class="key">Serial Number&nbsp;:</span></dt>
            <dd class="value clearfix"><div class="preserve-white-space">X7WM023100</div></dd></dl>
            """;

        (string? serial, string? firmware, string? status, string? consumables) =
            PrinterFollowUps.ParseEpsonPrinterInfo(html);

        Assert.Equal("X7WM023100", serial);
        Assert.Equal("04.55.KI03P9", firmware);
        Assert.Equal("Available.", status);
        Assert.Equal("MK:ok|LGY:low", consumables); // maintenance box (no clrname) excluded
    }

    [Fact]
    public void ParseEpsonHardwareAlerts_SurfacesOnlyNonNormalLines()
    {
        const string html =
            """
            <dl class="values"><dt class="key"><span class="key">Wi-Fi&nbsp;:</span></dt>
            <dd class="value clearfix"><div class="preserve-white-space">Working normally.</div></dd></dl>
            """;
        Assert.Null(PrinterFollowUps.ParseEpsonHardwareAlerts(html)); // "normally" → no alert

        const string degraded =
            """
            <dl class="values"><dt class="key"><span class="key">Wi-Fi&nbsp;:</span></dt>
            <dd class="value clearfix"><div class="preserve-white-space">Signal weak.</div></dd></dl>
            """;
        Assert.Equal("Wi-Fi:Signal weak.", PrinterFollowUps.ParseEpsonHardwareAlerts(degraded));
    }

    // ── Multi-page fetch orchestration ──────────────────────────────────────────

    [Fact]
    public async Task FetchHpAsync_CombinesAllThreeDocuments()
    {
        Uri baseUri = new("https://192.168.1.234/");
        Dictionary<string, string> pages = new()
        {
            ["/DevMgmt/ProductConfigDyn.xml"] =
                """
                <prdcfgdyn:ProductConfigDyn xmlns:dd="http://www.hp.com/schemas/imaging/con/dictionaries/1.0/"
                    xmlns:prdcfgdyn="http://www.hp.com/schemas/imaging/con/ledm/productconfigdyn/2007/11/05">
                  <prdcfgdyn:ProductInformation>
                    <dd:MakeAndModel>OfficeJet Pro 6978 All-in-One</dd:MakeAndModel>
                    <dd:SerialNumber>TH8891P11C</dd:SerialNumber>
                    <dd:ProductNumber>T0F29A</dd:ProductNumber>
                  </prdcfgdyn:ProductInformation>
                </prdcfgdyn:ProductConfigDyn>
                """,
            ["/DevMgmt/ProductStatusDyn.xml"] =
                """
                <psdyn:ProductStatusDyn xmlns:pscat="http://www.hp.com/schemas/imaging/con/ledm/productstatuscategories/2007/10/31"
                    xmlns:psdyn="http://www.hp.com/schemas/imaging/con/ledm/productstatusdyn/2007/10/31">
                  <psdyn:Status><pscat:StatusCategory>inPowerSave</pscat:StatusCategory></psdyn:Status>
                </psdyn:ProductStatusDyn>
                """,
            ["/DevMgmt/ConsumableConfigDyn.xml"] =
                """
                <ccdyn:ConsumableConfigDyn xmlns:dd="http://www.hp.com/schemas/imaging/con/dictionaries/1.0/"
                    xmlns:ccdyn="http://www.hp.com/schemas/imaging/con/ledm/consumableconfigdyn/2007/11/19">
                  <ccdyn:ConsumableInfo>
                    <dd:ConsumableTypeEnum>ink</dd:ConsumableTypeEnum>
                    <dd:ConsumableLabelCode>K</dd:ConsumableLabelCode>
                    <dd:ConsumableLifeState><dd:MeasuredQuantityState>ok</dd:MeasuredQuantityState></dd:ConsumableLifeState>
                  </ccdyn:ConsumableInfo>
                </ccdyn:ConsumableConfigDyn>
                """,
        };

        (HttpDeepFields? identity, PrinterFollowUps.PrinterDetails? details) = await PrinterFollowUps.FetchHpAsync(
            baseUri,
            (url, _) => Task.FromResult(pages.GetValueOrDefault(url.AbsolutePath)),
            CancellationToken.None
        );

        Assert.NotNull(identity);
        Assert.Equal("HP", identity.Vendor);
        Assert.Equal("TH8891P11C", identity.Serial);
        Assert.NotNull(details);
        Assert.Equal("T0F29A", details.ProductNumber);
        Assert.Equal("inPowerSave", details.Status);
        Assert.Equal("K:ok", details.Consumables);
    }

    [Fact]
    public async Task FetchHpAsync_MissingDocuments_StillReturnsWhateverSucceeded()
    {
        Uri baseUri = new("https://192.168.1.234/");

        (HttpDeepFields? identity, PrinterFollowUps.PrinterDetails? details) = await PrinterFollowUps.FetchHpAsync(
            baseUri,
            (_, _) => Task.FromResult<string?>(null), // every fetch fails
            CancellationToken.None
        );

        Assert.Null(identity);
        Assert.Null(details);
    }

    [Fact]
    public async Task FetchEpsonAsync_CombinesLandingAndInfoPages()
    {
        Uri baseUri = new("http://192.168.1.92/");
        Dictionary<string, string> pages = new()
        {
            ["/PRESENTATION/ADVANCED/COMMON/TOP"] = """<h1 id="model_name">SC-P900 Series</h1>""",
            ["/PRESENTATION/ADVANCED/INFO_PRTINFO/TOP"] =
                """
                <fieldset class="group"><legend>Printer Status</legend><ul class="values"><li class="value clearfix">
                <div class="preserve-white-space">Available.</div></li></ul></fieldset>
                <dl class="values"><dt class="key"><span class="key">Serial Number&nbsp;:</span></dt>
                <dd class="value clearfix"><div class="preserve-white-space">X7WM023100</div></dd></dl>
                """,
            ["/PRESENTATION/ADVANCED/INFO_BEHAVIORINFO/TOP"] =
                """
                <dl class="values"><dt class="key"><span class="key">Wi-Fi&nbsp;:</span></dt>
                <dd class="value clearfix"><div class="preserve-white-space">Working normally.</div></dd></dl>
                """,
        };

        (HttpDeepFields? identity, PrinterFollowUps.PrinterDetails? details) = await PrinterFollowUps.FetchEpsonAsync(
            baseUri,
            (url, _) => Task.FromResult(pages.GetValueOrDefault(url.AbsolutePath)),
            CancellationToken.None
        );

        Assert.NotNull(identity);
        Assert.Equal("Epson", identity.Vendor);
        Assert.Equal("SC-P900 Series", identity.Model);
        Assert.Equal("X7WM023100", identity.Serial);
        Assert.NotNull(details);
        Assert.Equal("Available.", details.Status);
        Assert.Null(details.Alerts); // Wi-Fi normal → no alert
    }
}