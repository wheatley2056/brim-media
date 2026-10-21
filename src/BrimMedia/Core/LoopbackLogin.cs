using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BrimMedia.Core;

/// <summary>
/// Wartet auf die Rückkehr des Browsers nach der Spotify-Anmeldung (<c>GET /callback?code=…&amp;state=…</c>).
/// Ein schlichter TCP-Listener auf 127.0.0.1 statt <see cref="HttpListener"/>, weil der unter Windows
/// für 127.0.0.1 eine URL-Reservierung mit Adminrechten bräuchte.
/// </summary>
public static class LoopbackLogin
{
    public sealed record Result(string? Code, string? State, string? Error);

    public static async Task<Result> WaitAsync(int port, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, cancellationToken);
                if (target is null || !target.StartsWith("/callback", StringComparison.Ordinal))
                {
                    // favicon.ico und Ähnliches
                    await RespondAsync(stream, 404, "", cancellationToken);
                    continue;
                }
                var result = Parse(target);
                var ok = result.Code is not null;
                await RespondAsync(stream, 200, Page(ok), cancellationToken);
                return result;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static Result Parse(string target)
    {
        var question = target.IndexOf('?');
        var values = new Dictionary<string, string>();
        if (question >= 0)
        {
            foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                values.TryAdd(key, value);
            }
        }
        return new Result(values.GetValueOrDefault("code"), values.GetValueOrDefault("state"), values.GetValueOrDefault("error"));
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Nur die erste Zeile zählt: "GET /callback?code=… HTTP/1.1". Den Rest des Kopfes lesen, damit der Browser nicht hängt.
        var buffer = new byte[8192];
        var read = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
            if (n == 0)
            {
                break;
            }
            read += n;
            if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }
        var head = Encoding.ASCII.GetString(buffer, 0, read);
        var line = head.Split("\r\n")[0].Split(' ');
        return line.Length >= 2 && line[0] == "GET" ? line[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static string Page(bool ok) =>
        "<!doctype html><meta charset=utf-8><title>Brim Media+</title>" +
        "<body style=\"background:#000;color:#F2F3F5;font:16px 'Segoe UI',sans-serif;display:grid;place-items:center;height:90vh\">" +
        (ok ? "<p>Mit Spotify verbunden. Du kannst dieses Fenster schließen.</p>"
            : "<p>Die Anmeldung wurde abgebrochen. Du kannst dieses Fenster schließen.</p>") +
        "</body>";
}
