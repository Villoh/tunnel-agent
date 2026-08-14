using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Infrastructure.Engine.NineRouter;

/// <summary>
/// Loopback HTTP listener that captures an OAuth authorization <c>code</c> from
/// the provider redirect. Used for Claude and Gemini CLI; GitHub Copilot uses
/// device-code polling instead.
/// </summary>
public sealed class OAuthCallbackListener : IDisposable
{
    private readonly TcpListener _listener;
    private bool _disposed;

    private OAuthCallbackListener(TcpListener listener, string redirectUri)
    {
        _listener = listener;
        RedirectUri = redirectUri;
    }

    /// <summary>Gets the <c>http://127.0.0.1:{port}/callback</c> redirect URI.</summary>
    public string RedirectUri { get; }

    /// <summary>Binds a loopback port and starts accepting one callback request.</summary>
    public static OAuthCallbackListener Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new OAuthCallbackListener(listener, $"http://127.0.0.1:{port}/callback");
    }

    /// <summary>
    /// Waits for the browser redirect and returns the authorization code.
    /// Does not log the code.
    /// </summary>
    /// <param name="timeout">How long to wait for the redirect.</param>
    /// <param name="ct">Token used to cancel the wait.</param>
    /// <returns>The <c>code</c> query value.</returns>
    public async Task<string> WaitForCodeAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        TcpClient client;
        try
        {
            client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NineRouterApiException(
                HttpStatusCode.RequestTimeout,
                "OAuth timed out before the browser redirect arrived.");
        }

        using (client)
        {
            client.ReceiveTimeout = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
            using var stream = client.GetStream();
            var request = await ReadHttpRequestAsync(stream, linked.Token).ConfigureAwait(false);
            var query = ParseRequestTarget(request);
            query.TryGetValue("error", out var error);
            if (!string.IsNullOrEmpty(error))
            {
                query.TryGetValue("error_description", out var description);
                await WriteHtmlAsync(stream, "Authorization was denied.", linked.Token).ConfigureAwait(false);
                throw new NineRouterApiException(
                    HttpStatusCode.BadRequest,
                    string.IsNullOrWhiteSpace(description) ? error : description);
            }

            query.TryGetValue("code", out var code);
            if (string.IsNullOrWhiteSpace(code))
            {
                await WriteHtmlAsync(stream, "Missing authorization code.", linked.Token).ConfigureAwait(false);
                throw new NineRouterApiException(HttpStatusCode.BadRequest, "OAuth redirect did not include an authorization code.");
            }

            await WriteHtmlAsync(stream, "You can close this window and return to Tunnel Agent.", linked.Token)
                .ConfigureAwait(false);
            return code;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already stopped.
        }
    }

    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (ms.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
                break;
            ms.Write(buffer, 0, read);
            var text = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                return text;
        }

        throw new NineRouterApiException(HttpStatusCode.BadRequest, "OAuth callback request was empty or incomplete.");
    }

    private static Dictionary<string, string> ParseRequestTarget(string request)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var firstLine = request.Split('\r', '\n')[0];
        var parts = firstLine.Split(' ');
        if (parts.Length < 2)
            return result;

        var target = parts[1];
        var q = target.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
            return result;

        var query = target[(q + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static async Task WriteHtmlAsync(NetworkStream stream, string message, CancellationToken ct)
    {
        var body = "<!DOCTYPE html><html><body><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
