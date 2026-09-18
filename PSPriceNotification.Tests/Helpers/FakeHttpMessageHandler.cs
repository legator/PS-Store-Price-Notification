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
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _handlerFunc;

    public FakeHttpMessageHandler(HttpStatusCode statusCode, string body = "")
    {
        _statusCode = statusCode;
        _body = body;
    }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
    {
        _handlerFunc = handlerFunc;
        _statusCode = HttpStatusCode.OK;
        _body = "";
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_handlerFunc != null)
            return Task.FromResult(_handlerFunc(request));

        var response = new HttpResponseMessage(_statusCode)
        {
            RequestMessage = request,
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "text/html"),
        };
        return Task.FromResult(response);
    }
}
