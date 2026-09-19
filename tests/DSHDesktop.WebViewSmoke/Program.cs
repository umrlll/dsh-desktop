using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using DSHDesktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DSHDesktop.WebViewSmoke;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        var userData = Path.Combine(Path.GetTempPath(), "dshdesktop-webview-smoke-" + Guid.NewGuid().ToString("N"));
        using var server = new ProbeServer();
        using var form = new Form
        {
            Text = "DSHDesktop WebView smoke",
            Width = 800,
            Height = 600,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000),
        };
        using var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);

        Exception? failure = null;
        var passed = false;
        using var timeout = new System.Windows.Forms.Timer { Interval = 30_000 };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            failure = new TimeoutException("WebView2 smoke did not complete within 30 seconds.");
            form.Close();
        };

        form.Shown += async (_, _) =>
        {
            try
            {
                timeout.Start();
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
                await web.EnsureCoreWebView2Async(environment);
                web.CoreWebView2.Settings.AreDevToolsEnabled = false;

                if (!WebViewPolicy.TryCreateTrustedOrigin(server.Url, out var origin) || origin == null)
                    throw new InvalidOperationException("Probe server did not produce a trusted loopback origin.");

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                web.NavigationCompleted += async (_, args) =>
                {
                    try
                    {
                        if (!args.IsSuccess)
                            throw new InvalidOperationException("Navigation failed: " + args.WebErrorStatus);
                        if (!WebViewPolicy.IsTrusted(web.Source?.AbsoluteUri, origin))
                            throw new InvalidOperationException("WebView left the owned loopback origin.");

                        var raw = await web.CoreWebView2.ExecuteScriptAsync(FrontendHealthProbe.Script);
                        if (FrontendHealthProbe.Parse(raw) != FrontendHealthProbe.Result.Healthy)
                            throw new InvalidOperationException("Interactive surface probe returned " + raw);

                        passed = true;
                        completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        completion.TrySetResult();
                    }
                };

                web.CoreWebView2.Navigate(server.Url);
                await completion.Task;
                form.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
                form.Close();
            }
        };

        Application.Run(form);
        timeout.Stop();
        try { Directory.Delete(userData, recursive: true); } catch { /* WebView may release files asynchronously. */ }

        if (!passed)
        {
            Console.Error.WriteLine("WEBVIEW_SMOKE_FAILED: " + failure);
            return 1;
        }

        Console.WriteLine("WEBVIEW_SMOKE_OK: trusted navigation and interactive surface confirmed at " + server.Url);
        return 0;
    }

    private sealed class ProbeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveTask;

        internal ProbeServer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = "http://127.0.0.1:" + port + "/";
            _serveTask = ServeAsync(_cts.Token);
        }

        internal string Url { get; }

        private async Task ServeAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(token); }
                catch (OperationCanceledException) { return; }

                _ = RespondAsync(client, token);
            }
        }

        private static async Task RespondAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var request = new byte[8192];
                _ = await stream.ReadAsync(request, token);
                const string html = "<!doctype html><html><body><div id='root'><button>Ready</button></div></body></html>";
                var body = Encoding.UTF8.GetBytes(html);
                var headers = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "
                    + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, token);
                await stream.WriteAsync(body, token);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _serveTask.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }
}
