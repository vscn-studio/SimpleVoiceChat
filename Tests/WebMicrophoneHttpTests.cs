using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SimpleVoiceChat.Networking;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class WebMicrophoneHttpTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task ModDoesNotHostLauncherWebPage(string path)
    {
        using var response = await RequestAsync(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HealthIdentifiesVoiceService()
    {
        using var response = await RequestAsync(HttpMethod.Get, "/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("SimpleVoiceChat.WebMicrophone", json.RootElement.GetProperty("service").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("protocol").GetInt32());
    }

    [Theory]
    [InlineData("GET", "/not-a-page", 404)]
    [InlineData("POST", "/", 405)]
    [InlineData("HEAD", "/health", 200)]
    public async Task UnsupportedRequestsAndHeadHaveNoBody(string method, string path, int status)
    {
        using var response = await RequestAsync(new HttpMethod(method), path);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<HttpResponseMessage> RequestAsync(HttpMethod method, string path)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        string origin = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(origin);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var request = new HttpRequestMessage(method, origin.TrimEnd('/') + path);
        var response = client.SendAsync(request, timeout.Token);
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        await WebMicrophoneHttpPage.WriteAsync(context.Request, context.Response, timeout.Token);
        return await response;
    }
}
