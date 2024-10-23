// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Security.Claims;

namespace osu.Server.BeatmapSubmission.Authentication
{
    public static class AuthenticationExtensions
    {
        public static int GetUserId(this ClaimsPrincipal principal)
        {
            if (!principal.HasClaim(c => c.Type == ClaimTypes.NameIdentifier))
                throw new InvalidOperationException($"Provided {nameof(ClaimsPrincipal)} does not have the {nameof(ClaimTypes.NameIdentifier)} claim.");

            return int.Parse(principal.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value);
        }
    }
}
