// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using osu.Server.BeatmapSubmission.Authentication;

namespace osu.Server.BeatmapSubmission
{
    public class UserAllowListFilter : IActionFilter
    {
        private readonly HashSet<uint> allowedUserIds;

        public UserAllowListFilter(IEnumerable<uint> allowedUserIds)
        {
            this.allowedUserIds = new HashSet<uint>(allowedUserIds);
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (!context.HttpContext.User.HasClaim(c => c.Type == ClaimTypes.NameIdentifier)
                || !allowedUserIds.Contains(context.HttpContext.User.GetUserId()))
            {
                context.Result = new ForbidResult();
            }
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }
}
