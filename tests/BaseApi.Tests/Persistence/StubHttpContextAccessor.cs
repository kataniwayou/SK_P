using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// Hand-written test double for <see cref="IHttpContextAccessor"/>.
/// REQUIREMENTS.md Out of Scope excludes Moq/NSubstitute in v1; this is the
/// minimal stub Phase 3 tests need.
///
/// <para>
/// <b>SC#3 usage:</b> set a "logged-in" user via <see cref="SetUser"/> and the
/// AuditInterceptor will stamp CreatedBy with that name. Pass null (or leave
/// <see cref="HttpContext"/> null) to simulate non-HTTP execution; CreatedBy
/// stamps null with no exception (PERSIST-04 + D-08).
/// </para>
/// </summary>
public sealed class StubHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }

    /// <summary>
    /// Convenience: set HttpContext to a DefaultHttpContext whose User has the given Name.
    /// Pass null to clear (simulates "no HttpContext" — non-HTTP execution path).
    /// </summary>
    public void SetUser(string? name)
    {
        if (name is null)
        {
            HttpContext = null;
            return;
        }

        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        var principal = new ClaimsPrincipal(identity);

        HttpContext = new DefaultHttpContext
        {
            User = principal
        };
    }
}
