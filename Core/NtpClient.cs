using System.Net;
using System.Net.Sockets;

namespace GoMidi.Core;

/// <summary>
/// Minimal SNTP client used to align scheduled playback with a real-world clock.
/// Queries servers in order and returns the first usable response together with
/// the measured clock offset. Ported from <c>NtpClient.cpp</c>.
/// </summary>
public sealed class NtpClient : IDisposable
{
    private const int NtpPort = 123;
    private const int TimeoutMs = 3000;

    private static readonly string[] Servers =
    [
        "ntp.aliyun.com",
        "cn.pool.ntp.org",
        "time.windows.com",
        "pool.ntp.org",
    ];

    // Seconds between 1900-01-01 (NTP epoch) and 1970-01-01 (Unix epoch).
    private static readonly DateTimeOffset NtpEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private bool _disposed;

    /// <summary>Offset to add to the local clock to obtain authoritative time.</summary>
    public TimeSpan Offset { get; private set; } = TimeSpan.Zero;

    /// <summary>Local clock plus <see cref="Offset"/>.</summary>
    public DateTimeOffset NetworkTime => DateTimeOffset.Now + Offset;

    public bool IsSynchronized { get; private set; }
    public string LastServer { get; private set; } = string.Empty;
    public double RoundTripMs { get; private set; }
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Synchronizes against the first reachable server.
    /// </summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns><c>true</c> when the offset was refreshed.</returns>
    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        foreach (string server in Servers)
        {
            try
            {
                (TimeSpan offset, double rtt) = await QueryAsync(server, cancellationToken).ConfigureAwait(false);
                Offset = offset;
                RoundTripMs = rtt;
                LastServer = server;
                IsSynchronized = true;
                LastError = string.Empty;
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller gave up entirely.
                throw;
            }
            catch (Exception ex)
            {
                // Covers a per-server receive timeout as well as network errors:
                // a slow or unreachable server must not abort the whole sync.
                LastError = $"{server}: {ex.Message}";
            }
        }

        IsSynchronized = false;
        return false;
    }

    private static async Task<(TimeSpan Offset, double Rtt)> QueryAsync(string server, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(server, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new IOException("无法解析服务器地址");
        }

        IPAddress address = addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(address, NtpPort);

        using var socket = new UdpClient(address.AddressFamily);
        socket.Connect(endpoint);

        var request = new byte[48];
        request[0] = 0x1B; // LI = 3 (unsynchronized), VN = 3, Mode = 3 (client)

        long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await socket.SendAsync(request, cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutMs);

        UdpReceiveResult result = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
        long t3 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        byte[] response = result.Buffer;
        if (response.Length < 48)
        {
            throw new IOException("响应长度不足");
        }

        // Stratum 0 means a Kiss-o'-Death packet.
        int stratum = response[1];
        if (stratum == 0)
        {
            throw new IOException("服务器返回 KoD 数据包");
        }

        uint originate = ReadU32(response, 24);
        uint transmit = ReadU32(response, 40);
        if (transmit == 0)
        {
            throw new IOException("服务器未返回时间戳");
        }

        DateTimeOffset serverTime = NtpEpoch.AddSeconds(transmit).AddSeconds(Fraction(response, 40));
        long serverMs = serverTime.ToUnixTimeMilliseconds();
        long localMs = t0 + (t3 - t0) / 2;

        _ = originate;
        return (TimeSpan.FromMilliseconds(serverMs - localMs), t3 - t0);
    }

    private static uint ReadU32(byte[] buffer, int offset) =>
        (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);

    private static double Fraction(byte[] buffer, int offset)
    {
        uint fraction = ReadU32(buffer, offset + 4);
        return fraction / 4294967296.0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
