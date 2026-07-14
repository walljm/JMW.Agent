using JMW.Discovery.Agent;

namespace JMW.Discovery.Tests;

/// <summary>
/// security-02 (plaintext HTTP credential transport): the heartbeat response carries decrypted
/// device credentials and every request carries this agent's API key, so a plain-http
/// server_url must be rejected unless the operator explicitly opts in for lab/dev use.
/// </summary>
public sealed class AgentConfigTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"agent-config-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private void WriteConfig(string json) => File.WriteAllText(_path, json);

    [Fact]
    public void LoadFrom_AcceptsHttpsServerUrl()
    {
        WriteConfig("""{"server_url": "https://monitor.example.com", "name": "a"}""");
        AgentConfig config = AgentConfig.LoadFrom(_path);
        Assert.Equal("https://monitor.example.com", config.ServerUrl);
    }

    [Fact]
    public void LoadFrom_ThrowsForHttpServerUrl_WhenNotExplicitlyAllowed()
    {
        WriteConfig("""{"server_url": "http://monitor.example.com", "name": "a"}""");
        Assert.Throws<InvalidOperationException>(() => AgentConfig.LoadFrom(_path));
    }

    [Fact]
    public void LoadFrom_ThrowsForHttpLocalhost_WhenNotExplicitlyAllowed()
    {
        WriteConfig("""{"server_url": "http://localhost:8090", "name": "a"}""");
        Assert.Throws<InvalidOperationException>(() => AgentConfig.LoadFrom(_path));
    }

    [Fact]
    public void LoadFrom_AllowsHttpServerUrl_WhenExplicitlyOptedIn()
    {
        WriteConfig(
            """{"server_url": "http://localhost:8090", "name": "a", "allow_insecure_http": true}"""
        );
        AgentConfig config = AgentConfig.LoadFrom(_path);
        Assert.Equal("http://localhost:8090", config.ServerUrl);
        Assert.True(config.AllowInsecureHttp);
    }
}
