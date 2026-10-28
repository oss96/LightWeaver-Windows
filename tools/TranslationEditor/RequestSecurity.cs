using Microsoft.AspNetCore.Http;

namespace TranslationEditor;

public static class RequestSecurity
{
    /// <summary>The status to refuse the request with, or null to let it through: 401 when an API
    /// call lacks this user's session, 403 for a foreign host or an unsafe write.</summary>
    public static int? Refusal(HttpRequest request, EditorSession session)
    {
        if (request.Host.Host is not ("127.0.0.1" or "localhost")) return StatusCodes.Status403Forbidden;
        if (request.Path.StartsWithSegments("/api") && !session.Matches(request.Headers[EditorSession.HeaderName]))
            return StatusCodes.Status401Unauthorized;
        if (!HttpMethods.IsPost(request.Method) && !HttpMethods.IsPut(request.Method)) return null;
        var origin = request.Headers.Origin.ToString();
        return request.Headers["X-Translation-Editor"] == "1" && request.HasJsonContentType()
            && (origin.Length == 0 || origin == "http://" + request.Host)
            ? null
            : StatusCodes.Status403Forbidden;
    }
}
