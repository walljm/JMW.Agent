using System.Reflection;

using JMW.Discovery.Server.Infrastructure;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// <see cref="OuiUpdateService.ExtractCountryCode" /> pulls a country out of the IEEE
/// registry's free-text "Organization Address" field. That field has no structured
/// country column — the code is a positional convention ("...City State CC Zip") with
/// real-world edge cases: US state abbreviations that collide with country codes
/// (California "CA" vs. Canada "CA"), Dutch zip codes whose letter suffix looks like a
/// country ("1032 LA"), and fully-capitalized addresses where a connector word ("DE",
/// "LA") reads as uppercase. These cases were found by running the heuristic against
/// the full live IEEE oui/mam/oui36/iab registries before picking the final algorithm.
/// </summary>
public sealed class OuiCountryExtractionTests
{
    private static string? Extract(string address)
    {
        MethodInfo m = typeof(OuiUpdateService).GetMethod(
                "ExtractCountryCode",
                BindingFlags.NonPublic | BindingFlags.Static
            )
         ?? throw new InvalidOperationException("OuiUpdateService.ExtractCountryCode not found.");
        return (string?)m.Invoke(null, [address]);
    }

    public static IEnumerable<object?[]> Cases()
    {
        // Plain "...City State CC Zip" — the common case.
        yield return ["7760 France Ave S Suite 340 Bloomington MN US 55438", "US"];
        yield return ["No.388 Ning Qiao Road,Jin Qiao Pudong Shanghai Shanghai   CN 201206", "CN"];

        // California ("CA") precedes the real country ("US") — must not be mistaken
        // for Canada's ISO code, which happens to also be "CA".
        yield return ["80 West Tasman Drive San Jose CA US 94568", "US"];

        // Dutch zip format "1234 AB": the letter suffix ("VA") coincidentally matches a
        // real ISO code (Vatican City) — the true country ("NL") sits one token further
        // left, immediately before the digit run.
        yield return ["Nieuw Amsterdamsestraat 40 Emmen Drenthe NL 7814 VA", "NL"];
        yield return ["Overcingellaan 7 Assen Drenthe NL 9401 LA", "NL"];

        // Fully-capitalized street name containing a foreign-language connector word
        // ("DE LA") must not be read as a country when a real code ends the address.
        yield return ["SANT JOAN DE LA SALLE 6   ES", "ES"];

        // No zip code at all — the country is simply the last token.
        yield return ["307 Harbour Centre, Tower 1,   HK", "HK"];

        // Malta zip has a 3-4 char alnum token after the country; country isn't the
        // last token but is still found by the left-scan window.
        yield return ["167 Merchants Street Valletta  MT VLT 1174", "MT"];

        // Legacy rows that spell the country out as a word instead of an ISO code
        // aren't resolvable by this heuristic and should return null, not a guess.
        yield return ["WERNER-VON-SIEMENS STRASSE 13    GERMANY", null];
        yield return ["4-21 MINAMI NARUSE", null];

        // No address at all (e.g. "Private" registrations with an empty address field).
        yield return ["", null];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExtractsExpectedCountry(string address, string? expected) =>
        Assert.Equal(expected, Extract(address));
}