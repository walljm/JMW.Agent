using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Socket plumbing shared by the network scanners (review D6): a TCP connect-with-timeout and the
/// UDP send-then-receive-until-timeout loop that were copy-pasted across ~15 scanners. Each helper
/// owns only the transport mechanics; the caller keeps its protocol-specific request/parse logic.
/// </summary>
public static class SocketProbe
{
    /// <summary>
    /// Opens a TCP connection to <paramref name="ip" />:<paramref name="port" />, bounded by
    /// <paramref name="timeoutMs" /> and linked to <paramref name="ct" />. Returns the connected
    /// <see cref="TcpClient" /> (the caller owns and disposes it), or null if the connection failed
    /// or timed out. Never throws for an unreachable host.
    /// </summary>
    public static async Task<TcpClient?> TryConnectAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        TcpClient tcp = new();
        try
        {
            using CancellationTokenSource timeout = new(timeoutMs);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(ip, port, linked.Token);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Yields UDP datagrams received on <paramref name="udp" /> until <paramref name="timeoutMs" />
    /// elapses or <paramref name="ct" /> fires — the standard "collect all responses to a broadcast/
    /// multicast query" loop. The caller sends its query first, then enumerates; parsing and any
    /// size/shape filtering stay with the caller.
    /// </summary>
    public static async IAsyncEnumerable<UdpReceiveResult> CollectResponsesAsync(
        UdpClient udp,
        int timeoutMs,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        while (true)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break; // timeout or caller cancellation — stop, keep what we have
            }
            catch (SocketException)
            {
                break; // e.g. ICMP port-unreachable reset mid-scan — stop, keep partial results
            }

            yield return result;
        }
    }
}