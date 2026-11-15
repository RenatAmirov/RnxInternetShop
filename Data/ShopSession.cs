using Microsoft.AspNetCore.DataProtection;

namespace RnxInternetShop.Data;

public sealed class ShopSession(IHttpContextAccessor accessor, IDataProtectionProvider protection)
{
    private const string CookieName = "rnx-shop.session";
    private Guid? owner;

    public CancellationToken Cancellation => Context.RequestAborted;
    private HttpContext Context => accessor.HttpContext ?? throw new InvalidOperationException("No HTTP request.");

    public Guid Owner
    {
        get
        {
            if (owner is { } current)
                return current;

            var protector = protection.CreateProtector(CookieName);
            if (Context.Request.Cookies.TryGetValue(CookieName, out var cookie))
            {
                try
                {
                    if (Guid.TryParse(protector.Unprotect(cookie), out var existing))
                        return (owner = existing).Value;
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Issue a new session if the cookie is invalid or its key was retired.
                }
            }

            var id = Guid.NewGuid();
            Context.Response.Cookies.Append(CookieName, protector.Protect(id.ToString()), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = Context.Request.IsHttps,
                MaxAge = TimeSpan.FromDays(180),
                Path = "/"
            });
            return (owner = id).Value;
        }
    }
}
