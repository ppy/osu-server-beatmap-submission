// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Security.Claims;

namespace osu.Server.BeatmapSubmission.Authentication
{
    public static class AuthenticationExtensions
    {
        public const string USER_ID_CLAIM_TYPE = "osu_user_id";

        public static uint GetUserId(this ClaimsPrincipal principal)
        {
            if (!principal.HasClaim(c => c.Type == USER_ID_CLAIM_TYPE))
                throw new InvalidOperationException($"Provided {nameof(ClaimsPrincipal)} does not have the {USER_ID_CLAIM_TYPE} claim.");

            return uint.Parse(principal.Claims.Single(c => c.Type == USER_ID_CLAIM_TYPE).Value);
        }
    }
}
