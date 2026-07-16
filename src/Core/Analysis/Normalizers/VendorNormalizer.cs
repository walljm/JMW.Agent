namespace JMW.Discovery.Core.Analysis.Normalizers;

/// <summary>
/// Canonicalizes manufacturer/vendor name strings to a consistent, human-readable
/// proper-case form. Raw values vary wildly by collection source for what is
/// ultimately the same handful of companies — DMI/SMBIOS ("Dell Inc.",
/// "ASUSTeK COMPUTER INC."), /proc/cpuinfo vendor_id ("GenuineIntel"), ONVIF/UPnP
/// manufacturer strings, BACnet/Modbus vendor registries, IEEE-style all-caps
/// forms ("GOOGLE, INC.").
/// This is intentionally NOT a lowercase/trim transform — the goal is the
/// vendor's own preferred display name (e.g. "Google", "Dell", "AMD"), not a
/// case-folded token.
/// Pipeline: trim -> reject "no real value" placeholders -> strip a trailing
/// legal-entity suffix (", Inc.", " Corporation", " Co., Ltd.", ...) -> if the
/// result matches a known vendor in <see cref="Aliases" />, return its canonical
/// name. Suffix stripping is mechanical and always applied — it's a safe,
/// reversible cleanup regardless of whether we recognize the vendor. Renaming
/// (the alias table) is the part that requires recognition: a vendor we've
/// never seen still gets a value back (suffix-stripped, original casing), it
/// just doesn't get remapped to a different display name we haven't vetted.
/// </summary>
public sealed class VendorNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns =>
    [
        FactPaths.DeviceVendor,
        FactPaths.HwCpuVendor,
        FactPaths.HwBoardVendor,
        FactPaths.HwSystemVendor,
        FactPaths.HwBiosVendor,
        FactPaths.HwChassisVendor,
        FactPaths.HwComponentVendor,
        FactPaths.GpuVendor,
        FactPaths.BacnetVendorName,
        FactPaths.ModbusVendorName,
        FactPaths.DiscoveredVendor,
    ];

    public FactValue? Normalize(FactValue raw)
    {
        string? str = raw.AsString();
        if (str is null)
        {
            return null;
        }

        string trimmed = str.Trim();
        if (trimmed.Length == 0 || Junk.Contains(trimmed))
        {
            return null;
        }

        // Suffix stripping is mechanical (safe regardless of whether we recognize
        // the vendor); alias lookup is the only step gated on recognition.
        string stripped = StripLegalSuffixes(trimmed);
        if (stripped.Length == 0)
        {
            return null;
        }

        return FactValue.FromString(Aliases.TryGetValue(stripped, out string? canonical) ? canonical : stripped);
    }

    // "No real vendor" placeholders. DMI/SMBIOS-sourced fields are already run
    // through DmiDecode.Clean() at collection time (see HardwareCollector.cs), but
    // SNMP/ONVIF/UPnP/BACnet/Modbus vendor strings reach this normalizer raw.
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown",
        "n/a",
        "none",
        "not specified",
        "not applicable",
        "to be filled by o.e.m.",
        "default string",
        "system manufacturer",
        "system product name",
        "oem",
        "generic",
    };

    private static string StripLegalSuffixes(string value)
    {
        string current = value;
        bool changed;
        do
        {
            changed = false;
            foreach (string suffix in LegalSuffixes)
            {
                if (current.Length > suffix.Length && current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    current = current[..^suffix.Length].TrimEnd(' ', ',', '.');
                    changed = true;
                    break;
                }
            }
        } while (changed && current.Length > 0);

        return current;
    }

    // Longest/most-specific first so e.g. ", Co., Ltd." is stripped whole rather
    // than leaving a dangling ", Co.". Each entry requires a leading space or
    // comma so we never clip mid-word (" Co." won't match the tail of "Cisco").
    private static readonly string[] LegalSuffixes =
    [
        ", Co., Ltd.", " Co., Ltd.", ", Co. Ltd.", " Co. Ltd.", ", Co Ltd", " Co Ltd",
        ", Corporation", " Corporation",
        ", Incorporated", " Incorporated",
        ", Inc.", " Inc.", ", Inc", " Inc",
        ", Corp.", " Corp.", ", Corp", " Corp",
        ", Ltd.", " Ltd.", ", Ltd", " Ltd", " Limited",
        ", LLC", " LLC", ", L.L.C.", " L.L.C.",
        " GmbH", " AG", " S.A.", " SA", " N.V.", " NV",
        " Pty. Ltd.", " Pty Ltd",
        " Co.",
    ];

    // Case-insensitive lookup keyed on the suffix-stripped value; the value is
    // the canonical display name. Built from real raw values seen in this
    // codebase's collectors (DMI manufacturer strings, /proc/cpuinfo vendor_id,
    // hardcoded literals) plus the common home/SMB network vendors this system
    // identifies via SNMP/ONVIF/UPnP/mDNS. Extend as new vendors surface.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // CPU vendor_id tokens (/proc/cpuinfo) vs SMBIOS processor manufacturer
        ["GenuineIntel"] = "Intel",
        ["AuthenticAMD"] = "AMD",
        ["Advanced Micro Devices"] = "AMD",

        // Board/system/BIOS manufacturers (DMI) — legal-name and all-caps variants
        ["ASUSTeK Computer"] = "ASUS",
        ["Hewlett-Packard"] = "HP",
        ["Hewlett Packard"] = "HP",
        ["Hewlett Packard Enterprise"] = "HPE",
        ["Micro-Star International"] = "MSI",
        ["Gigabyte Technology"] = "Gigabyte",
        ["Super Micro Computer"] = "Supermicro",
        ["Lenovo"] = "Lenovo",
        ["Dell"] = "Dell",
        ["Nvidia"] = "NVIDIA",
        ["Qemu"] = "QEMU",

        // Home/SMB network + IoT vendors surfaced via SNMP/ONVIF/UPnP/mDNS/BACnet.
        // These are curated pairs, not a generic "strip business words" rule —
        // dropping a descriptor like "Networks"/"Systems" is only safe once we've
        // confirmed the shortened form is the vendor's actual common name.
        ["Google"] = "Google",
        ["Apple"] = "Apple",
        ["Microsoft"] = "Microsoft",
        ["Amazon"] = "Amazon",
        ["Samsung Electronics"] = "Samsung",
        ["Ubiquiti Networks"] = "Ubiquiti",
        ["TP-Link Technologies"] = "TP-Link",
        ["Netgear"] = "NETGEAR",
        ["Cisco Systems"] = "Cisco",
        ["Arista Networks"] = "Arista",
        ["Juniper Networks"] = "Juniper",
        ["Synology"] = "Synology",
        ["QNAP Systems"] = "QNAP",
        ["Philips"] = "Philips",
        ["Roku"] = "Roku",
        ["Sonos"] = "Sonos",

        // IANA Private Enterprise Numbers registrant names (raw, via SnmpCollector's
        // sysObjectID lookup — see EnterpriseNumberRegistry / vendor-derivation-updates.md
        // §2.5) don't follow any consistent naming convention, so each needs its own mapping
        // to the canonical form already used by this plan's other vendor derivations.
        ["ciscoSystems"] = "Cisco", // IANA's literal registrant name for enterprise 9
        ["American Power Conversion"] = "APC", // suffix-stripped from "...Corp."
        ["MikroTik"] = "Mikrotik", // IANA casing differs from this codebase's canonical "Mikrotik"
        ["TP-Link Systems"] = "TP-Link", // distinct legal name from the "TP-Link Technologies" DMI form
        ["D-Link Systems"] = "D-Link",
        ["PALO ALTO NETWORKS"] = "Palo Alto Networks", // IANA registrant name is all-caps
        ["Aruba, a Hewlett Packard Enterprise company"] = "Aruba", // no legal-suffix pattern matches this

        // Ported from ITPIE.DeviceAnalysis's NodeOperatingSystem.Normalize.cs GetVendor
        // (exact-match cases only — the free-text substring/regex catch-all half of that
        // project's logic lives in VendorOsFromDeviceBannerDerivation
        // instead, since that's the JMW analog of scanning e.g. Device[].SNMP.SysDescr). Keys and
        // canonical values are that project's own already-vetted forms, carried over as-is.
        // Entries that would collide with an existing key above (dell, lenovo, apple, microsoft,
        // amazon, netgear, mikrotik, super micro computer) are skipped — this codebase's
        // already-established canonical choice wins over the reference project's differing one.
        ["3com"] = "3Com",
        ["a10"] = "A10",
        ["acctontechnology"] = "AcctonTechnology",
        ["acgate"] = "Acgate",
        ["aaeon"] = "AAEON",
        ["aastra"] = "Aastra",
        ["acme"] = "Acme",
        ["adtran"] = "Adtran",
        ["advantech"] = "Advantech",
        ["aerohivenetworks"] = "AerohiveNetworks",
        ["afl"] = "AFL",
        ["airspan"] = "Airspan",
        ["airtight"] = "AirTight",
        ["akcp"] = "AKCP",
        ["alaxala"] = "AlaxalA",
        ["alcatellucent"] = "AlcatelLucent",
        ["ale"] = "ALE",
        ["allied telesis"] = "Allied Telesis",
        ["alvarion ltd"] = "Alvarion Ltd",
        ["apc"] = "APC",
        ["amgsystems"] = "AMGSystems",
        ["ancor"] = "Ancor",
        ["anda"] = "Anda",
        ["apcon"] = "Apcon",
        ["appgate"] = "AppGate",
        ["appliedinnovations"] = "AppliedInnovations",
        ["arbornetworks"] = "ArborNetworks",
        ["arista"] = "Arista",
        ["arraynetworks"] = "ArrayNetworks",
        ["arrisinternational"] = "ArrisInternational",
        ["aruba"] = "Aruba",
        ["asantetechnology"] = "AsanteTechnology",
        ["asentria"] = "Asentria",
        ["aten"] = "ATEN",
        ["attotechnology"] = "ATTOTechnology",
        ["audiocodes"] = "AudioCodes",
        ["avaya"] = "Avaya",
        ["avicisystems"] = "AviciSystems",
        ["avocentcorporation"] = "AvocentCorporation",
        ["axiscommunications"] = "AxisCommunications",
        ["bachmann"] = "Bachmann",
        ["barixag"] = "BarixAG",
        ["barracuda"] = "Barracuda",
        ["batm"] = "BATM",
        ["baynetworks"] = "BayNetworks",
        ["bdt"] = "BDT",
        ["bintecelmeg"] = "BintecElmeg",
        ["blackboxcorporation"] = "BlackBoxCorporation",
        ["big switch"] = "Big Switch",
        ["bluecoat"] = "BlueCoat",
        ["bluecatnetworks"] = "BlueCatNetworks",
        ["bluesocket"] = "Bluesocket",
        ["bosch"] = "Bosch",
        ["broadcom"] = "Broadcom",
        ["brocade"] = "Brocade",
        ["bsdunix"] = "BsdUnix",
        ["bytemobile"] = "Bytemobile",
        ["catechnologies"] = "CATechnologies",
        ["cabletron"] = "Cabletron",
        ["cairs"] = "CAIRS",
        ["calixnetworks"] = "CalixNetworks",
        ["caldera"] = "Caldera",
        ["cambium"] = "Cambium",
        ["canogaperkins"] = "CanogaPerkins",
        ["canon"] = "Canon",
        ["canonical"] = "Canonical",
        ["centos"] = "CentOS",
        ["cascade"] = "Cascade",
        ["cayman"] = "CAYMAN",
        ["ceragon"] = "Ceragon",
        ["chatsworthproducts"] = "ChatsworthProducts",
        ["check point"] = "Check Point",
        ["ciena"] = "Ciena",
        ["cipheroptics"] = "Cipheroptics",
        ["cisco"] = "Cisco",
        ["citrix"] = "Citrix",
        ["clarent"] = "Clarent",
        ["cnt"] = "CNT",
        ["commvault"] = "Commvault",
        ["compatible"] = "Compatible",
        ["compoint"] = "ComPoint",
        ["comtec systems"] = "Comtec Systems",
        ["copper"] = "Copper",
        ["corecess"] = "Corecess",
        ["corvil"] = "Corvil",
        ["cloudgenix"] = "CloudGenix",
        ["commscope"] = "CommScope",
        ["connect tech"] = "Connect Tech",
        ["coreos"] = "CoreOS",
        ["corevalent"] = "Corevalent",
        ["cosine"] = "Cosine",
        ["coyotepoint"] = "CoyotePoint",
        ["cradlepoint"] = "Cradlepoint",
        ["cray"] = "Cray",
        ["crossbeam"] = "Crossbeam",
        ["crestron"] = "Crestron",
        ["crossroads"] = "CrossRoads",
        ["cyclades"] = "Cyclades",
        ["cylink"] = "Cylink",
        ["cubic"] = "Cubic",
        ["cumulus"] = "Cumulus",
        ["cypresssolutions"] = "CypressSolutions",
        ["dasan"] = "Dasan",
        ["datamax"] = "Datamax",
        ["dec"] = "DEC",
        ["debian"] = "Debian",
        ["delphix"] = "Delphix",
        ["deveicon"] = "DeveIcon",
        ["develop"] = "DEVELOP",
        ["dialogic"] = "Dialogic",
        ["digiinternational"] = "DigiInternational",
        ["digilink"] = "Digilink",
        ["docker"] = "Docker",
        ["dualcom"] = "Dualcom",
        ["east"] = "EAST",
        ["easternresearch"] = "EasternResearch",
        ["eaton"] = "Eaton",
        ["ecomserver"] = "eComServer",
        ["edgewaternetworks"] = "EdgewaterNetworks",
        ["efficientip"] = "EfficientIP",
        ["engage"] = "Engage",
        ["enterasysnetworks"] = "EnterasysNetworks",
        ["epson"] = "Epson",
        ["ericsson"] = "Ericsson",
        ["exagrid"] = "ExaGrid",
        ["edgecore"] = "Edgecore",
        ["expandnetworks"] = "ExpandNetworks",
        ["extreme"] = "Extreme",
        ["extronelectronics"] = "ExtronElectronics",
        ["f5"] = "F5",
        ["farallon"] = "Farallon",
        ["fibrolan"] = "FibroLAN",
        ["fibronics"] = "Fibronics",
        ["firstvirtual"] = "FirstVirtual",
        ["flowpoint"] = "FlowPoint",
        ["flukenetworks"] = "FlukeNetworks",
        ["fireeye"] = "FireEye",
        ["forescouttechnologies"] = "ForeScoutTechnologies",
        ["forcepoint"] = "Forcepoint",
        ["fortinet"] = "Fortinet",
        ["foundry"] = "Foundry",
        ["fourrf"] = "FourRF",
        ["fujitsu"] = "Fujitsu",
        ["gandalf"] = "Gandalf",
        ["ganymede"] = "Ganymede",
        ["garrettcom"] = "GarrettCom",
        ["gdms"] = "GDMS",
        ["geist"] = "Geist",
        ["generalelectric"] = "GeneralElectric",
        ["generaldatacomm"] = "GeneralDataComm",
        ["genua"] = "Genua",
        ["gigamon"] = "Gigamon",
        ["h3c"] = "H3C",
        ["hanaa"] = "HanaA",
        ["hanwhatechwin"] = "HanwhaTechwin",
        ["hatterasnetworks"] = "HatterasNetworks",
        ["hp"] = "HP",
        ["hpe"] = "HPE",
        ["hikvision"] = "Hikvision",
        ["hillstonenetworks"] = "HillstoneNetworks",
        ["hirschmann"] = "Hirschmann",
        ["hitachi"] = "Hitachi",
        ["hmsindustrialnetworks"] = "HMSIndustrialNetworks",
        ["honeywellinternational"] = "HoneywellInternational",
        ["huawei"] = "Huawei",
        ["hubbellpulsecom"] = "HubbellPulsecom",
        ["hughes"] = "Hughes",
        ["hwgroup"] = "HWgroup",
        ["hypercomcorporation"] = "HypercomCorporation",
        ["ibm"] = "IBM",
        ["impinj"] = "Impinj",
        ["infoblox"] = "Infoblox",
        ["inkra"] = "Inkra",
        ["intel"] = "Intel",
        ["intermectechnologies"] = "IntermecTechnologies",
        ["ipc"] = "IPC",
        ["iqinvision"] = "IqinVision",
        ["itwatchdogs"] = "ITWatchDogs",
        ["ixia"] = "Ixia",
        ["inventec"] = "INVENTEC",
        ["iridium"] = "Iridium",
        ["ivanti"] = "Ivanti",
        ["juniper"] = "Juniper",
        ["kagoor"] = "Kagoor",
        ["kemptechnologies"] = "KempTechnologies",
        ["kentrox"] = "Kentrox",
        ["konicaminolta"] = "KonicaMinolta",
        ["klas"] = "Klas",
        ["krcs"] = "KRCS",
        ["kymeta"] = "Kymeta",
        ["kyocera"] = "Kyocera",
        ["lancast"] = "Lancast",
        ["l3harris"] = "L3Harris",
        ["lancom"] = "LANCOM",
        ["lantronix"] = "Lantronix",
        ["larscom"] = "Larscom",
        ["laurel"] = "Laurel",
        ["levelone"] = "LevelOne",
        ["lexmark"] = "Lexmark",
        ["liebertcorporation"] = "LiebertCorporation",
        ["linksys"] = "Linksys",
        ["liveaction"] = "LiveAction",
        ["luxn"] = "LuxN",
        ["madge"] = "Madge",
        ["maipu"] = "Maipu",
        ["marconi"] = "Marconi",
        ["mcafee"] = "McAfee",
        ["mellanox"] = "Mellanox",
        ["memoteccommunications"] = "MemotecCommunications",
        ["merunetworks"] = "MeruNetworks",
        ["mge"] = "MGE",
        ["microframe"] = "MicroFrame",
        ["micromuse"] = "Micromuse",
        ["microsemicorporation"] = "MicrosemiCorporation",
        ["milantechnology"] = "MiLANTechnology",
        ["mirapoint"] = "MiraPoint",
        ["mitel"] = "MITEL",
        ["motorola"] = "Motorola",
        ["mist"] = "Mist",
        ["mitac"] = "MiTAC",
        ["mojo"] = "Mojo",
        ["moxa"] = "Moxa",
        ["mrvcommunications"] = "MRVCommunications",
        ["nai"] = "NAI",
        ["ncr"] = "NCR",
        ["nec"] = "NEC",
        ["netapp"] = "NetApp",
        ["netbotz"] = "NetBotz",
        ["netplus"] = "NetPlus",
        ["netscout"] = "Netscout",
        ["netstar"] = "NetStar",
        ["networkalchemy"] = "NetworkAlchemy",
        ["networkequipmenttechnologies"] = "NetworkEquipmentTechnologies",
        ["networkgeneral"] = "NetworkGeneral",
        ["newbridgenetworks"] = "NewbridgeNetworks",
        ["niksun"] = "Niksun",
        ["nokia"] = "Nokia",
        ["nortel"] = "Nortel",
        ["novell"] = "Novell",
        ["nuera"] = "Nuera",
        ["oce"] = "Oce",
        ["oki"] = "OKI",
        ["omnitron"] = "Omnitron",
        ["omnitronix"] = "Omnitronix",
        ["oneaccess"] = "OneAccess",
        ["opengear"] = "OpenGear",
        ["opensoftwarefoundation"] = "OpenSoftwareFoundation",
        ["opensuse"] = "OpenSUSE",
        ["oracle"] = "Oracle",
        ["ovzon"] = "Ovzon",
        ["overture"] = "Overture",
        ["packeteer"] = "Packeteer",
        ["packetfrontsystems"] = "PacketFrontSystems",
        ["palo alto"] = "Palo Alto Networks", // aligned with this codebase's existing canonical form (see VendorLockedOsNames)
        ["paloalto"] = "Palo Alto Networks",
        ["panasas"] = "Panasas",
        ["paradyne"] = "Paradyne",
        ["parkscomunicacoesdigitais"] = "ParksComunicacoesDigitais",
        ["pattonelectronics"] = "PattonElectronics",
        ["peplink"] = "Peplink",
        ["perle"] = "Perle",
        ["persistent"] = "Persistent",
        ["plaintree"] = "PlainTree",
        ["polycom"] = "Polycom",
        ["pluribus"] = "Pluribus",
        ["powerdsine"] = "PowerDsine",
        ["precidia"] = "Precidia",
        ["premiernetwork"] = "PremierNetwork",
        ["printronix"] = "Printronix",
        ["proteon"] = "Proteon",
        ["proximwireless"] = "ProximWireless",
        ["qlogic"] = "Qlogic",
        ["qms"] = "QMS",
        ["quickeagle"] = "QuickEagle",
        ["racaldatacomm"] = "RacalDatacomm",
        ["raddatacommunications"] = "RADDataCommunications",
        ["radware"] = "Radware",
        ["radwin"] = "Radwin",
        ["raritan"] = "Raritan",
        ["redbacknetworks"] = "RedBackNetworks",
        ["redcreek"] = "RedCreek",
        ["redlinecommunications"] = "RedlineCommunications",
        ["redlinenetworks"] = "RedLineNetworks",
        ["redstonecommunications"] = "RedstoneCommunications",
        ["rittal"] = "RITTAL",
        ["ransnet"] = "RansNet",
        ["red hat"] = "Red Hat",
        ["riverbed"] = "Riverbed",
        ["riverhead"] = "Riverhead",
        ["rocky esf"] = "Rocky ESF",
        ["romeo6"] = "Romeo6",
        ["riverstonenetworks"] = "RiverstoneNetworks",
        ["rockwellautomation"] = "RockwellAutomation",
        ["rtbrick"] = "RtBrick",
        ["ruckus"] = "Ruckus",
        ["ruijie"] = "Ruijie",
        ["salixtechnologies"] = "SALIXTechnologies",
        ["samsung"] = "Samsung",
        ["sandvine"] = "Sandvine",
        ["satelcom"] = "Satelcom",
        ["schneiderelectric"] = "SchneiderElectric",
        ["sensatronics"] = "Sensatronics",
        ["servertechnology"] = "ServerTechnology",
        ["sgi"] = "SGI",
        ["sharp"] = "Sharp",
        ["sco"] = "SCO",
        ["shenzhencdatatechnology"] = "ShenzhenCDataTechnology",
        ["shiva"] = "Shiva",
        ["siaemicroelettronica"] = "SIAEMicroelettronica",
        ["siemens"] = "Siemens",
        ["silverpeaksystems"] = "SilverPeakSystems",
        ["silvus"] = "Silvus",
        ["sitara"] = "Sitara",
        ["skyline"] = "Skyline",
        ["smartoptics"] = "Smartoptics",
        ["smcnetworks"] = "SMCNetworks",
        ["socomec"] = "Socomec",
        ["sonusnetworks"] = "SonusNetworks",
        ["specialix"] = "Specialix",
        ["spectracomcorporation"] = "SpectracomCorporation",
        ["springtidenetworks"] = "SpringTideNetworks",
        ["stalecom"] = "Stalecom",
        ["stulz"] = "Stulz",
        ["sumitomo"] = "Sumitomo",
        ["stl"] = "STL",
        ["sun microsystems"] = "SUN Microsystems",
        ["sundray"] = "Sundray",
        ["suse"] = "SUSE",
        ["symantec"] = "Symantec",
        ["symmetricom"] = "Symmetricom",
        ["syncresearch"] = "SyncResearch",
        ["tandem"] = "Tandem",
        ["techroutesnetwork"] = "TechroutesNetwork",
        ["tejasnetworks"] = "TejasNetworks",
        ["tektronix"] = "Tektronix",
        ["teldat"] = "Teldat",
        ["telebit"] = "Telebit",
        ["telindus"] = "Telindus",
        ["tellabs"] = "Tellabs",
        ["teracomtelematica"] = "TeracomTelematica",
        ["teradici"] = "Teradici",
        ["teraspek"] = "Teraspek",
        ["tgv"] = "TGV",
        ["thales"] = "Thales",
        ["tiaranetworks"] = "TiaraNetworks",
        ["tibco"] = "TIBCO",
        ["timestep"] = "Timestep",
        ["tintri"] = "Tintri",
        ["trellisware"] = "TrellisWare",
        ["trend micro"] = "Trend Micro",
        ["toplayernetworks"] = "TopLayerNetworks",
        ["topspin"] = "Topspin",
        ["toshiba"] = "Toshiba",
        ["transitionnetworks"] = "TransitionNetworks",
        ["transmode"] = "Transmode",
        ["trapezenetworks"] = "TrapezeNetworks",
        ["tripplite"] = "TrippLite",
        ["tsc"] = "TSC",
        ["tubraunschweig"] = "TUBraunschweig",
        ["tutsystems"] = "TutSystems",
        ["tyan"] = "Tyan",
        ["ubnetworks"] = "UBNetworks",
        ["ubiquiti"] = "Ubiquiti",
        ["ucopia"] = "Ucopia",
        ["unisys"] = "Unisys",
        ["unixsystem"] = "UnixSystem",
        ["uplogix"] = "Uplogix",
        ["ushapro"] = "Ushapro",
        ["vbrick"] = "Vbrick",
        ["vegastream"] = "Vegastream",
        ["verilinkcorp"] = "VerilinkCorp",
        ["vernier"] = "Vernier",
        ["versanetworks"] = "VersaNetworks",
        ["verso"] = "Verso",
        ["visualnetworks"] = "VisualNetworks",
        ["via"] = "VIA",
        ["viasat"] = "Viasat",
        ["viptela"] = "Cisco",
        ["vmware"] = "VMware",
        ["vssmonitoring"] = "VSSMonitoring",
        ["watchguardtechnologies"] = "WatchGuardTechnologies",
        ["wind river"] = "Wind River",
        ["wavecom"] = "Wavecom",
        ["westernmultiplex"] = "WesternMultiplex",
        ["widebank"] = "Widebank",
        ["wirelessinnovations"] = "WirelessInnovations",
        ["witnesssystems"] = "WitnessSystems",
        ["xerox"] = "Xerox",
        ["xinuos"] = "Xinuos",
        ["xlntdesigns"] = "XlntDesigns",
        ["xylogics"] = "Xylogics",
        ["xyplex"] = "Xyplex",
        ["yamaha"] = "Yamaha",
        ["zebra"] = "Zebra",
        ["zhonetechnologies"] = "ZhoneTechnologies",
        ["zpesystems"] = "ZPESystems",
        ["zenitel"] = "Zenitel",
        ["zte"] = "ZTE",
        ["zyxel"] = "ZyXEL",
    };
}