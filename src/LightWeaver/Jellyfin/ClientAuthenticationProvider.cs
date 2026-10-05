using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace LightWeaver.Jellyfin;

/// <summary>Reads the service's client identity and token for each SDK request.</summary>
internal sealed class ClientAuthenticationProvider(JellyfinService service) : IAuthenticationProvider
{
    public Task AuthenticateRequestAsync(RequestInformation request,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        request.Headers.Remove("Authorization");
        request.Headers.Add("Authorization", service.AuthorizationHeader);
        return Task.CompletedTask;
    }
}
