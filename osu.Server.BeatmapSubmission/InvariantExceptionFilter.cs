// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.AspNetCore.Mvc.Filters;
using osu.Server.BeatmapSubmission.Models;

namespace osu.Server.BeatmapSubmission
{
    public class InvariantExceptionFilter : IExceptionFilter
    {
        private readonly ILogger<InvariantExceptionFilter> logger;

        public InvariantExceptionFilter(ILogger<InvariantExceptionFilter> logger)
        {
            this.logger = logger;
        }

        public void OnException(ExceptionContext context)
        {
            if (context.Exception is InvariantException invariantException)
            {
                context.Result = invariantException.ToResponseObject().ToActionResult();
                logger.Log(invariantException.Severity, context.Exception, "Request rejected due to violated invariant");
            }
        }
    }
}
