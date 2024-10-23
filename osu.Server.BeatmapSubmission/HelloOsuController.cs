// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using osu.Server.BeatmapSubmission.Authentication;
using osu.Server.QueueProcessor;

namespace osu.Server.BeatmapSubmission
{
    [Route("hello")]
    [Produces("application/json")]
    public class HelloOsuController : Controller
    {
        [HttpGet]
        public async Task<ulong> GetBeatmapSetCountAsync()
        {
            using var db = DatabaseAccess.GetConnection();

            return await db.QuerySingleAsync<ulong>("SELECT COUNT(1) FROM `osu_beatmapsets`");
        }

        [HttpGet("me")]
        [Authorize]
        public object GetMeAsync()
        {
            return new
            {
                Id = User.GetUserId(),
            };
        }
    }
}
