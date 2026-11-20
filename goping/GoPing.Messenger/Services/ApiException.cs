using System.Net;

namespace GoPing.Services;

public sealed class ApiException : Exception
{
    public string Code { get; }
    public HttpStatusCode? StatusCode { get; }

    public ApiException(string code, string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}
