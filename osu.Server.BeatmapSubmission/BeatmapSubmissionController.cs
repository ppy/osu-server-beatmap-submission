// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using osu.Framework.Extensions;
using osu.Game.IO.Archives;
using osu.Server.BeatmapSubmission.Authentication;
using osu.Server.BeatmapSubmission.Models.API.Requests;
using osu.Server.BeatmapSubmission.Models.API.Responses;
using osu.Server.BeatmapSubmission.Services;
using osu.Server.QueueProcessor;

namespace osu.Server.BeatmapSubmission
{
    [Authorize]
    public class BeatmapSubmissionController : Controller
    {
        private readonly IBeatmapStorage beatmapStorage;
        private readonly BeatmapPackagePatcher patcher;

        public BeatmapSubmissionController(
            IBeatmapStorage beatmapStorage,
            BeatmapPackagePatcher patcher)
        {
            this.beatmapStorage = beatmapStorage;
            this.patcher = patcher;
        }

        [HttpPut]
        [Route("beatmapsets")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public async Task<IActionResult> PutBeatmapSetAsync([FromBody] PutBeatmapSetRequest request)
        {
            using var db = DatabaseAccess.GetConnection();
            using var transaction = await db.BeginTransactionAsync();

            // TODO: check silence state (going to need to get the source to `osu.check_silenced()` function in db because i can't see into it)
            // TODO: check restriction state (`SELECT user_warnings FROM phpbb_users WHERE user_id = $userId`)
            // TODO: check difficulty limit (128 max)
            // TODO: check playcount (`("SELECT sum(playcount) FROM osu_user_month_playcount WHERE user_id = $userId") < 5`)
            // TODO: clean up user's inactive maps
            // TODO: check remaining map quota
            // TODO: create relevant forum threads or whatever so that people can put descriptions

            uint userId = User.GetUserId();

            uint? beatmapSetId = request.BeatmapSetID;
            uint[] existingBeatmaps = [];

            if (beatmapSetId == null)
            {
                if (request.BeatmapsToKeep.Any())
                    return UnprocessableEntity("Cannot specify beatmaps to keep when creating a new beatmap set.");

                string username = await db.GetUsernameAsync(userId, transaction);
                beatmapSetId = await db.CreateBlankBeatmapSetAsync(userId, username, transaction);
            }
            else
            {
                var beatmapSet = await db.GetBeatmapSetAsync(beatmapSetId.Value, transaction);

                if (beatmapSet == null)
                    return NotFound();

                if (beatmapSet.user_id != userId)
                    return Forbid();

                existingBeatmaps = (await db.GetBeatmapIdsInSetAsync(beatmapSetId.Value, transaction)).ToArray();
            }

            if (request.BeatmapsToKeep.Except(existingBeatmaps).Any())
                return UnprocessableEntity("One of the beatmaps to keep does not belong to the specified set.");

            foreach (uint beatmapId in existingBeatmaps.Except(request.BeatmapsToKeep))
                await db.DeleteBeatmapAsync(beatmapId, transaction);

            var beatmapIds = new List<uint>(request.BeatmapsToKeep);

            for (int i = 0; i < request.BeatmapsToCreate; ++i)
            {
                uint beatmapId = await db.CreateBlankBeatmapAsync(userId, beatmapSetId.Value, transaction);
                beatmapIds.Add(beatmapId);
            }

            await transaction.CommitAsync();

            return Ok(new PutBeatmapSetResponse
            {
                BeatmapSetId = beatmapSetId.Value,
                BeatmapIds = beatmapIds,
                Files = beatmapStorage.ListBeatmapSetFiles(beatmapSetId.Value),
            });
        }

        [HttpPut]
        [Route("beatmapsets/{beatmapSetId}")]
        public async Task<IActionResult> ReplaceBeatmapSetAsync(
            [FromRoute] uint beatmapSetId,
            // TODO: this won't fly on production, biggest existing beatmap archives exceed buffering limits.
            // see: https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-8.0#small-and-large-files
            // using this for now just to get something going.
            [FromForm] IFormFile beatmapArchive)
        {
            // TODO: do all of the due diligence checks

            using var db = DatabaseAccess.GetConnection();

            var beatmapSet = await db.GetBeatmapSetAsync(beatmapSetId);
            if (beatmapSet == null)
                return NotFound();

            if (beatmapSet.user_id != User.GetUserId())
                return Forbid();

            using var beatmapStream = beatmapArchive.OpenReadStream();
            using var archiveReader = new ZipArchiveReader(beatmapStream);

            var parseResult = BeatmapPackageParser.Parse(beatmapSetId, archiveReader);
            using var transaction = await db.BeginTransactionAsync();

            // TODO: ensure these actually belong to the beatmap set
            foreach (var beatmapRow in parseResult.Beatmaps)
                await db.UpdateBeatmapAsync(beatmapRow, transaction);

            await db.UpdateBeatmapSetAsync(parseResult.BeatmapSet, transaction);

            await transaction.CommitAsync();
            // TODO: the ACID implications on this are... interesting...
            await beatmapStorage.StoreBeatmapSetAsync(beatmapSetId, await beatmapStream.ReadAllBytesToArrayAsync());
            return NoContent();
        }

        [HttpPatch]
        [Route("beatmapsets/{beatmapSetId}")]
        public async Task<IActionResult> PatchBeatmapSetAsync(
            [FromRoute] uint beatmapSetId,
            [FromForm] IFormFileCollection filesChanged,
            [FromForm] string[] filesDeleted)
        {
            using var db = DatabaseAccess.GetConnection();

            var beatmapSet = await db.GetBeatmapSetAsync(beatmapSetId);
            if (beatmapSet == null)
                return NotFound();

            if (beatmapSet.user_id != User.GetUserId())
                return Forbid();

            var beatmapStream = await patcher.PatchBeatmapSetAsync(beatmapSetId, filesChanged, filesDeleted);
            await updateBeatmapSetFromArchiveAsync(beatmapSetId, beatmapStream, db);
            return NoContent();
        }

        private async Task updateBeatmapSetFromArchiveAsync(uint beatmapSetId, MemoryStream beatmapStream, MySqlConnection db)
        {
            using var archiveReader = new ZipArchiveReader(beatmapStream);
            var parseResult = BeatmapPackageParser.Parse(beatmapSetId, archiveReader);
            using var transaction = await db.BeginTransactionAsync();

            // TODO: ensure these actually belong to the beatmap set
            foreach (var beatmapRow in parseResult.Beatmaps)
                await db.UpdateBeatmapAsync(beatmapRow, transaction);

            await db.UpdateBeatmapSetAsync(parseResult.BeatmapSet, transaction);

            await transaction.CommitAsync();
            // TODO: the ACID implications on this are... interesting...
            await beatmapStorage.StoreBeatmapSetAsync(beatmapSetId, await beatmapStream.ReadAllBytesToArrayAsync());
        }

        [HttpPatch]
        [Route("beatmapsets/{beatmapSetId}/beatmaps/{beatmapId}")]
        public async Task<IActionResult> PatchBeatmapAsGuestAsync(
            [FromRoute] uint beatmapSetId,
            [FromRoute] uint beatmapId,
            [FromForm] IFormFile beatmapContents)
        {
            using var db = DatabaseAccess.GetConnection();

            var beatmap = await db.GetBeatmapAsync(beatmapSetId, beatmapId);
            if (beatmap == null)
                return NotFound();

            // TODO: this is probably obsolete immediately once https://github.com/ppy/osu-web/pull/11377 goes in.
            // don't wanna get too deep into the woods on that one right now.
            if (beatmap.user_id != User.GetUserId())
                return Forbid();

            var archiveStream = await patcher.PatchBeatmapAsync(beatmapSetId, beatmapId, beatmapContents);
            await updateBeatmapSetFromArchiveAsync(beatmapSetId, archiveStream, db);
            return NoContent();
        }
    }
}
