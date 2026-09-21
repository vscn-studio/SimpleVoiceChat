using System.Net;
using System.Text;

namespace SimpleVoiceChat.Networking;

/// <summary>Health check for the loopback microphone backend. LauncherGo owns the web page.</summary>
internal static class WebMicrophoneHttpPage
{
    private static readonly byte[] Health = Encoding.UTF8.GetBytes("{\"service\":\"SimpleVoiceChat.WebMicrophone\",\"protocol\":1}");

    internal static async Task WriteAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        bool head = request.HttpMethod == "HEAD";
        if (request.HttpMethod != "GET" && !head)
        {
            response.StatusCode = 405;
            response.AddHeader("Allow", "GET, HEAD");
            response.Close();
            return;
        }
        var path = request.Url?.AbsolutePath;
        bool health = path == "/health";
        if (!health)
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }
        var content = Health;
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = content.Length;
        response.AddHeader("Cache-Control", "no-store");
        if (!head) await response.OutputStream.WriteAsync(content, cancellationToken);
        response.Close();
    }
}
