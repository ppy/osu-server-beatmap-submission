// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.AspNetCore.Mvc;

namespace osu.Server.BeatmapSubmission
{
    public class BeatmapSubmissionController : Controller
    {
        [HttpPut]
        [Route("beatmapsets/{beatmapSetId}")]
        public async Task<long> PutBeatmapSetAsync(
            [FromRoute] uint beatmapSetId,
            // TODO: this won't fly on production, biggest existing beatmap archives exceed buffering limits.
            // see: https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-8.0#small-and-large-files
            // using this for now just to get something going.
            [FromForm] IFormFile beatmapArchive)
        {
            return beatmapArchive.Length;
        }
    }
}
