using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Lightweight HTTP server that serves a branded success/error page
/// when the OAuth provider redirects back to localhost after authentication.
///
/// Usage: start before launching the OAuth binary, pass its port via
/// -oauth-callback-port. The server listens for one request, serves the
/// page, then shuts down (or times out after 5 minutes).
/// </summary>
public sealed class OAuthCallbackServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serveTask;

    public int Port { get; }

    public OAuthCallbackServer(int port)
    {
        Port      = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start(string providerName)
    {
        _listener.Start();
        _serveTask = ServeAsync(providerName, _cts.Token);
    }

    private async Task ServeAsync(string providerName, CancellationToken ct)
    {
        // Timeout: stop after 5 minutes regardless
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(linked.Token);
                var path    = context.Request.Url?.AbsolutePath ?? "/";
                var query   = context.Request.Url?.Query ?? "";

                var isError   = query.Contains("error=");
                var html      = isError
                    ? BuildErrorPage(providerName, query)
                    : BuildSuccessPage(providerName);

                var bytes = Encoding.UTF8.GetBytes(html);
                context.Response.ContentType     = "text/html; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.StatusCode      = 200;
                await context.Response.OutputStream.WriteAsync(bytes, linked.Token);
                context.Response.Close();

                // After a successful callback we're done
                if (!isError) break;
            }
        }
        catch (OperationCanceledException) { }
        catch { /* listener closed */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }

    // ── HTML pages ────────────────────────────────────────────────────────────

    private static string BuildSuccessPage(string providerName) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Connected — TunnelAgent</title>
          <style>
            *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
            body {
              font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
              background: #1a1a1f;
              color: #e8e8ea;
              display: flex;
              align-items: center;
              justify-content: center;
              min-height: 100vh;
            }
            .card {
              background: #25252c;
              border: 1px solid #333340;
              border-radius: 16px;
              padding: 48px 56px;
              text-align: center;
              max-width: 420px;
              width: 90%;
              animation: rise 0.4s cubic-bezier(0.16, 1, 0.3, 1);
            }
            @keyframes rise {
              from { opacity: 0; transform: translateY(16px); }
              to   { opacity: 1; transform: translateY(0); }
            }
            .icon {
              width: 64px;
              height: 64px;
              background: #1a3a2a;
              border-radius: 50%;
              display: flex;
              align-items: center;
              justify-content: center;
              margin: 0 auto 24px;
            }
            .icon svg { width: 32px; height: 32px; }
            h1 {
              font-size: 22px;
              font-weight: 600;
              margin-bottom: 10px;
              color: #f0f0f2;
            }
            p {
              font-size: 14px;
              color: #888898;
              line-height: 1.6;
            }
            .provider {
              display: inline-block;
              background: #2e2e38;
              border: 1px solid #3a3a48;
              border-radius: 6px;
              padding: 2px 10px;
              font-size: 13px;
              font-weight: 500;
              color: #c8c8d8;
              margin-bottom: 24px;
            }
            .close-hint {
              margin-top: 32px;
              font-size: 12px;
              color: #555568;
            }
          </style>
        </head>
        <body>
          <div class="card">
            <div class="icon">
              <svg viewBox="0 0 24 24" fill="none" stroke="#3cb371" stroke-width="2.5"
                   stroke-linecap="round" stroke-linejoin="round">
                <polyline points="20 6 9 17 4 12"/>
              </svg>
            </div>
            <span class="provider">{{providerName}}</span>
            <h1>Authentication successful</h1>
            <p>Your account has been connected.<br>
               You can close this tab and return to TunnelAgent.</p>
            <p class="close-hint">This tab will close automatically in a few seconds.</p>
          </div>
          <script>setTimeout(() => window.close(), 3000);</script>
        </body>
        </html>
        """;

    private static string BuildErrorPage(string providerName, string query)
    {
        var error       = ExtractParam(query, "error") ?? "unknown_error";
        var description = ExtractParam(query, "error_description") ?? "An error occurred during authentication.";

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Error — TunnelAgent</title>
          <style>
            *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
            body {
              font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
              background: #1a1a1f;
              color: #e8e8ea;
              display: flex;
              align-items: center;
              justify-content: center;
              min-height: 100vh;
            }
            .card {
              background: #25252c;
              border: 1px solid #333340;
              border-radius: 16px;
              padding: 48px 56px;
              text-align: center;
              max-width: 420px;
              width: 90%;
            }
            .icon {
              width: 64px;
              height: 64px;
              background: #3a1a1a;
              border-radius: 50%;
              display: flex;
              align-items: center;
              justify-content: center;
              margin: 0 auto 24px;
            }
            .icon svg { width: 32px; height: 32px; }
            h1 { font-size: 22px; font-weight: 600; margin-bottom: 10px; color: #f0f0f2; }
            p  { font-size: 14px; color: #888898; line-height: 1.6; }
            .provider {
              display: inline-block;
              background: #2e2e38;
              border: 1px solid #3a3a48;
              border-radius: 6px;
              padding: 2px 10px;
              font-size: 13px;
              font-weight: 500;
              color: #c8c8d8;
              margin-bottom: 24px;
            }
            code {
              display: block;
              margin-top: 16px;
              background: #1e1e26;
              border-radius: 6px;
              padding: 10px 14px;
              font-family: 'Cascadia Code', Consolas, monospace;
              font-size: 11px;
              color: #cc6666;
              text-align: left;
              word-break: break-all;
            }
          </style>
        </head>
        <body>
          <div class="card">
            <div class="icon">
              <svg viewBox="0 0 24 24" fill="none" stroke="#cc4444" stroke-width="2.5"
                   stroke-linecap="round" stroke-linejoin="round">
                <line x1="18" y1="6" x2="6" y2="18"/>
                <line x1="6" y1="6" x2="18" y2="18"/>
              </svg>
            </div>
            <span class="provider">{{providerName}}</span>
            <h1>Authentication failed</h1>
            <p>Something went wrong. Please try connecting again from TunnelAgent.</p>
            <code>{{HtmlEncode(error)}}: {{HtmlEncode(description)}}</code>
          </div>
        </body>
        </html>
        """;
    }

    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string? ExtractParam(string query, string key)
    {
        // Simple query string parser — no dependency on System.Web beyond HtmlEncode
        var q = query.TrimStart('?');
        foreach (var part in q.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            if (part[..eq] == key)
                return Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    // ── Port finder ───────────────────────────────────────────────────────────

    public static int FindFreePort(int preferredStart = 54200)
    {
        for (var port = preferredStart; port < preferredStart + 100; port++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
                l.Start();
                l.Stop();
                return port;
            }
            catch { }
        }
        return preferredStart;
    }
}
