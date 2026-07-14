using System.Net.WebSockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Device.HomeAssistant;

/// <summary>
/// <see cref="IHomeAssistantSocket" /> backed by the real <see cref="ClientWebSocket" />.
/// TLS validation routes through <see cref="CaTrust.Validate" /> — same private-CA trust
/// policy as the agent's other HTTPS collectors, since HA is commonly served on a
/// self-signed or private-CA certificate.
/// </summary>
public sealed class HomeAssistantClientWebSocket : IHomeAssistantSocket
{
    private readonly ClientWebSocket _ws = new();

    public HomeAssistantClientWebSocket()
    {
        _ws.Options.RemoteCertificateValidationCallback = CaTrust.Validate;
    }

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _ws.ConnectAsync(uri, ct);

    public Task SendAsync(string message, CancellationToken ct) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, ct);

    public async Task<string> ReceiveAsync(CancellationToken ct)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(chunk, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException(
                    $"Home Assistant closed the WebSocket ({_ws.CloseStatus}: {_ws.CloseStatusDescription})."
                );
            }

            buffer.Write(chunk, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Best-effort close — the connection may already be gone.
            }
        }

        _ws.Dispose();
    }
}
