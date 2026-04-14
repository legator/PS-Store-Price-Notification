using System.Net;

namespace PSPriceNotification.Tests.Helpers;

/// <summary>
/// A test double for HttpMessageHandler that returns a fixed response
/// without making real network calls.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public FakeHttpMessageHandler(HttpStatusCode statusCode, string body = "")
    {
        _statusCode = statusCode;
        _body = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "text/html"),
        };
        return Task.FromResult(response);
    }
}
