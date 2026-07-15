namespace JMW.Discovery.Agent.Collection.Device.HomeAssistant;

/// <summary>
/// Minimal WebSocket seam <see cref="HomeAssistantClient" /> talks through — one text message
/// in, one text message out. Real traffic is <see cref="HomeAssistantClientWebSocket" />
/// (backed by <see cref="System.Net.WebSockets.ClientWebSocket" />); tests inject a fake.
/// </summary>
public interface IHomeAssistantSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);

    Task SendAsync(string message, CancellationToken ct);

    /// <summary>Reads one complete WebSocket text message (concatenating frames until EndOfMessage).</summary>
    Task<string> ReceiveAsync(CancellationToken ct);
}