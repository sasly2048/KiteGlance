using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KiteGlance.Services;

/// <summary>
/// Minimal loopback HTTP responder that captures Kite's OAuth redirect.
/// Uses TcpListener (not HttpListener) so no URL-ACL / admin rights are needed.
/// Kite app Redirect URL must be: http://127.0.0.1:5173/callback
/// </summary>
public static class LoginServer
{
    public const int Port = 5173;

    private const string DonePage = """
        <!doctype html><html><head><meta charset="utf-8"><title>Kite Glance</title>
        <style>
        body{margin:0;height:100vh;display:grid;place-items:center;background:#0F1318;
             color:#fff;font-family:"Segoe UI",system-ui,sans-serif}
        .t{font-size:44px;color:#00D084}
        p{color:#A8B3BE}
        </style></head><body><div style="text-align:center">
        <div class="t">&#10003;</div><h2>Connected to Kite</h2>
        <p>You can close this tab and return to the widget.</p>
        </div></body></html>
        """;

    private const string FailedPage = """
        <!doctype html><html><head><meta charset="utf-8"><title>Kite Glance</title>
        <style>
        body{margin:0;height:100vh;display:grid;place-items:center;background:#0F1318;
             color:#fff;font-family:"Segoe UI",system-ui,sans-serif}
        .t{font-size:44px;color:#FF453A}
        p{color:#A8B3BE}
        </style></head><body><div style="text-align:center">
        <div class="t">&#10005;</div><h2>Sign-in was not completed</h2>
        <p>Kite cancelled or rejected the login. Return to the widget and try again.</p>
        </div></body></html>
        """;

    public static async Task<string> CaptureRequestTokenAsync(
        string loginUrl, int timeoutSeconds = 300)
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);

        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            throw new Exception(
                $"Port {Port} is busy. Close whatever is using it and try again.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true });

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(timeoutSeconds));

            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();

                // A browser can split the request across TCP segments, so a
                // single Read may return only part of the request line and
                // truncate the query string (dropping request_token). Read
                // until we have at least the end of the header block, or the
                // first line is unambiguously complete.
                var sb = new StringBuilder();
                var buffer = new byte[4096];
                while (sb.Length < 16384)
                {
                    var read = await stream.ReadAsync(buffer, cts.Token);
                    if (read == 0) break;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));

                    var soFar = sb.ToString();

                    // The end of the header block always means the whole
                    // request line has arrived.
                    if (soFar.Contains("\r\n\r\n")) break;

                    // Otherwise the request line is only complete once a CRLF
                    // appears *after* the " HTTP/" version token. The old test
                    // accepted any CRLF anywhere alongside any " HTTP/"
                    // anywhere, which a split TCP segment satisfies while the
                    // query string -- and with it request_token -- is still
                    // truncated.
                    var version = soFar.IndexOf(" HTTP/", StringComparison.Ordinal);
                    if (version >= 0 &&
                        soFar.IndexOf("\r\n", version, StringComparison.Ordinal) > version)
                        break;
                }

                var request = sb.ToString();
                var line = request.Split("\r\n")[0];          // GET /callback?... HTTP/1.1
                var parts = line.Split(' ');
                if (parts.Length < 2) continue;

                var path = parts[1];
                if (!path.StartsWith("/callback")) continue;

                var query = ParseQuery(path);

                query.TryGetValue("request_token", out var token);
                query.TryGetValue("status", out var status);

                var succeeded = status == "success" && !string.IsNullOrEmpty(token);

                // Decide first, then report. The page used to be sent before the
                // status was examined, so a rejected login still showed the user
                // a green "Connected to Kite" in the browser while the widget
                // said the opposite.
                var body = Encoding.UTF8.GetBytes(succeeded ? DonePage : FailedPage);
                var head = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n");

                // Writes are NOT passed cts.Token: a write that fails because
                // the browser tab was closed mid-response is a different
                // problem than the 5-minute idle timeout, and the user
                // deserves to know which one happened. The catch below only
                // sees the timeout-triggered OCE from AcceptTcpClientAsync.
                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
                await stream.FlushAsync();

                if (succeeded) return token!;

                throw new Exception("Login was cancelled or rejected by Kite.");
            }
        }
        catch (OperationCanceledException)
        {
            // The only OperationCanceledException in this method originates at
            // AcceptTcpClientAsync (the idle timer). Writes no longer take
            // the token, so a write failing because the browser tab was
            // closed mid-response surfaces as a plain IOException -- not as a
            // misleading "Login timed out".
            throw new Exception("Login timed out. Please try again.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var q = path.IndexOf('?');
        if (q < 0) return result;

        foreach (var pair in path[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;

            var k = Uri.UnescapeDataString(pair[..eq]);
            var v = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[k] = v;
        }

        return result;
    }
}
