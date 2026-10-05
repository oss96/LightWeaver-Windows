using Microsoft.AspNetCore.Http;

namespace TranslationEditor;

public static class RequestSecurity
{
    public static bool Allows(HttpRequest request)
    {
        if (request.Host.Host is not ("127.0.0.1" or "localhost")) return false;
        if (!HttpMethods.IsPost(request.Method) && !HttpMethods.IsPut(request.Method)) return true;
        var origin = request.Headers.Origin.ToString();
        return request.Headers["X-Translation-Editor"] == "1" && request.HasJsonContentType()
            && (origin.Length == 0 || origin == "http://" + request.Host);
    }
}
