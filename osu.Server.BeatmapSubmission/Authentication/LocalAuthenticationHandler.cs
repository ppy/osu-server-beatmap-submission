// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Security.Claims;
using System.Text.Encodings.Web;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace osu.Server.BeatmapSubmission.Authentication
{
    /// <summary>
    /// Used to authenticate in local development scenarios.
    /// The incoming user will receive a monotonically-increasing user ID,
    /// unless a <c>user_id</c> header is present in the request being authenticated,
    /// in which case the value of that header will be parsed as a number and used instead.
    /// </summary>
    /// <seealso href="https://github.com/ppy/osu-server-spectator/blob/9f8a00c0d85477e919382fe25bc2589e76166eec/osu.Server.Spectator/StartupDevelopment.cs"><c>StartupDevelopment</c> in <c>osu-server-spectator</c></seealso>
    [UsedImplicitly]
    public class LocalAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private static int userIDCounter = 2;

        /// <summary>
        /// The name of the authorisation scheme that this handler will respond to.
        /// </summary>
        public const string AUTH_SCHEME = "LocalAuth";

        public const string USER_ID_HEADER = "user_id";

        public LocalAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        /// <summary>
        /// Marks all authentication requests as successful, and injects required user claims.
        /// </summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var nameIdentifierClaim = createNameIdentifierClaim();

            var authenticationTicket = new AuthenticationTicket(
                new ClaimsPrincipal([new ClaimsIdentity([nameIdentifierClaim], AUTH_SCHEME)]),
                new AuthenticationProperties(), AUTH_SCHEME);

            return Task.FromResult(AuthenticateResult.Success(authenticationTicket));
        }

        private Claim createNameIdentifierClaim()
        {
            string? userIdString = null;

            if (Context.Request.Headers.TryGetValue(USER_ID_HEADER, out var userIdValue))
                userIdString = userIdValue;

            userIdString ??= Interlocked.Increment(ref userIDCounter).ToString();

            var nameIdentifierClaim = new Claim(ClaimTypes.NameIdentifier, userIdString);
            return nameIdentifierClaim;
        }
    }
}
