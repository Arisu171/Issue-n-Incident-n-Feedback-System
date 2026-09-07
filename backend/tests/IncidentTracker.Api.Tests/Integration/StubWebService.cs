using System.Net;
using System.Text;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Một service web giả, nghe HTTP thật trên localhost.
///
/// Cần nó vì health check của web gọi HTTP ra ngoài tiến trình, mà <c>TestServer</c> của
/// <c>WebApplicationFactory</c> lại không mở cổng mạng thật — không có stub này thì nhánh
/// "web healthy" của <c>/api/health/system</c> không kiểm chứng được ở tầng integration.
/// </summary>
public sealed class StubWebService : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// <paramref name="redirectToLogin"/> dựng lại đúng bối cảnh đã gặp khi deploy: web nằm sau
    /// một cổng đăng nhập, nên <c>/healthz</c> trả 302 và điểm đến cuối cùng lại là một trang
    /// 200 của bên thứ ba.
    /// </summary>
    public StubWebService(HttpStatusCode statusCode = HttpStatusCode.OK, bool redirectToLogin = false)
    {
        Port = GetFreePort();
        HealthUrl = $"http://127.0.0.1:{Port}/healthz";

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                // Cổng đăng nhập: /healthz bị đá sang /login, còn /login thì trả 200 ngon lành.
                if (redirectToLogin && context.Request.Url?.AbsolutePath == "/healthz")
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Found;
                    context.Response.RedirectLocation = $"http://127.0.0.1:{Port}/login";
                    context.Response.Close();
                    continue;
                }

                var body = Encoding.UTF8.GetBytes("""{"status":"Healthy","service":"web"}""");
                context.Response.StatusCode = (int)statusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
    }

    public int Port { get; }

    public string HealthUrl { get; }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }
        _listener.Close();
        _cts.Dispose();
    }
}
