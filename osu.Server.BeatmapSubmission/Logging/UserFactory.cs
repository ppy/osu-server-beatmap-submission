// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Globalization;
using System.Security.Claims;
using osu.Server.BeatmapSubmission.Authentication;

namespace osu.Server.BeatmapSubmission.Logging
{
    public class UserFactory : ISentryUserFactory
    {
        private readonly IHttpContextAccessor httpContextAccessor;

        public UserFactory(IHttpContextAccessor httpContextAccessor)
        {
            this.httpContextAccessor = httpContextAccessor;
        }

        public SentryUser? Create()
        {
            var user = httpContextAccessor.HttpContext?.User;

            if (user == null || !user.HasClaim(claim => claim.Type == ClaimTypes.NameIdentifier))
                return null;

            return new SentryUser
            {
                Id = user.GetUserId().ToString(CultureInfo.InvariantCulture),
            };
        }
    }
}
