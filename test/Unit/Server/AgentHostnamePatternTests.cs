using JMW.Discovery.Server.Agents;

namespace JMW.Discovery.Tests;

/// <summary>
/// security-01 (stored XSS via agent-supplied hostname): registration only length-checked the
/// hostname, so an inline-onclick-breaking payload reached the admin's browser. This covers the
/// RFC 1123 charset guard added in <see cref="AgentRegistrationEndpoint.HostnamePattern" />.
/// </summary>
public sealed class AgentHostnamePatternTests
{
    [Theory]
    [InlineData("router-1")]
    [InlineData("router-1.lan")]
    [InlineData("a")]
    [InlineData("192-168-1-1")]
    [InlineData("host.sub.example.com")]
    [InlineData("a1-b2.c3")]
    public void Matches_ValidHostnames(string hostname)
    {
        Assert.Matches(AgentRegistrationEndpoint.HostnamePattern, hostname);
    }

    [Theory]
    [InlineData("',null);alert(document.cookie);//")]
    [InlineData("</script><script>alert(1)</script>")]
    [InlineData("host'; alert(1); '")]
    [InlineData("host\"onmouseover=\"alert(1)")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    [InlineData("has space")]
    [InlineData("has_underscore")]
    [InlineData("")]
    [InlineData(".leadingdot")]
    [InlineData("double..dot")]
    public void RejectsMalformedOrInjectionHostnames(string hostname)
    {
        Assert.DoesNotMatch(AgentRegistrationEndpoint.HostnamePattern, hostname);
    }
}