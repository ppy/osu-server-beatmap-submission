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
using osu.Server.BeatmapSubmission.Models.Database;
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

        // TODO: accept pending/WIP user choice somewhere

        /// <summary>
        /// Create a new beatmap set, or add/remove beatmaps from an existing beatmap set
        /// </summary>
        /// <response code="200">The requested changes have been applied.</response>
        /// <response code="403">The user is not allowed to modify this beatmap set.</response>
        /// <response code="404">The request specified a beatmap set that does not yet exist.</response>
        /// <response code="429">The request was incorrectly formed. Check returned error for details.</response>
        [HttpPut]
        [Route("beatmapsets")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(PutBeatmapSetResponse), 200)]
        public async Task<IActionResult> PutBeatmapSetAsync([FromBody] PutBeatmapSetRequest request)
        {
            using var db = DatabaseAccess.GetConnection();
            using var transaction = await db.BeginTransactionAsync();

            // TODO: check silence state (going to need to get the source to `osu.check_silenced()` function in db because it doesn't exist in docker image)
            // TODO: check restriction state (`SELECT user_warnings FROM phpbb_users WHERE user_id = $userId`)
            // TODO: check difficulty limit (128 max)
            // TODO: check playcount (`("SELECT sum(playcount) FROM osu_user_month_playcount WHERE user_id = $userId") < 5`)
            // TODO: clean up user's inactive maps
            // TODO: check remaining map quota
            // TODO: create forum thread for description editing purposes if set is new

            uint userId = User.GetUserId();

            uint? beatmapSetId = request.BeatmapSetID;
            uint[] existingBeatmaps = [];

            if (beatmapSetId == null)
            {
                if (request.BeatmapsToKeep.Length != 0)
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

        /// <summary>
        /// Upload a full beatmap package (<c>.osz</c>) for the beatmap set with the given ID
        /// </summary>
        /// <param name="beatmapSetId" example="241526">The ID of the beatmap set.</param>
        /// <param name="beatmapArchive">The full beatmap package file.</param>
        /// <response code="204">The package has been successfully uploaded.</response>
        /// <response code="403">The user is not allowed to modify this beatmap set.</response>
        /// <response code="404">The request specified a beatmap set that does not yet exist.</response>
        [HttpPut]
        [Route("beatmapsets/{beatmapSetId}")]
        [ProducesResponseType(204)]
        public async Task<IActionResult> UploadFullPackageAsync(
            [FromRoute] uint beatmapSetId,
            // TODO: this won't fly on production, biggest existing beatmap archives exceed buffering limits (`MultipartBodyLengthLimit` = 128MB specifically)
            // potentially also https://github.com/aspnet/Announcements/issues/267
            // see: https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-8.0#small-and-large-files
            // needs further testing
            IFormFile beatmapArchive)
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
            // TODO: the ACID implications on this happening post-commit are... interesting... not sure anything can be done better?
            await beatmapStorage.StoreBeatmapSetAsync(beatmapSetId, await beatmapStream.ReadAllBytesToArrayAsync());
            return NoContent();
        }

        /// <summary>
        /// Perform a partial update to the package (<c>.osz</c>) for the beatmap set with the given ID
        /// </summary>
        /// <param name="beatmapSetId" example="241526">The ID of the beatmap set.</param>
        /// <param name="filesChanged">A collection of all changed files which should replace their previous versions in the package.</param>
        /// <param name="filesDeleted" example="[&quot;Kick.wav&quot;, &quot;Snare.wav&quot;]">A list of all filenames which should be deleted from the package.</param>
        /// <response code="204">The package has been successfully patched.</response>
        /// <response code="403">The user is not allowed to modify this beatmap set.</response>
        /// <response code="404">The request specified a beatmap set that does not yet exist.</response>
        [HttpPatch]
        [Route("beatmapsets/{beatmapSetId}")]
        [ProducesResponseType(204)]
        public async Task<IActionResult> PatchPackageAsync(
            [FromRoute] uint beatmapSetId,
            IFormFileCollection filesChanged,
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

            foreach (var file in parseResult.Files)
            {
                await db.InsertBeatmapsetFileAsync(new osu_beatmapset_file
                {
                    beatmapset_id = file.beatmapset_id,
                    sha2_hash = file.sha2_hash,
                }, transaction);
            }

            uint versionId = await db.CreateBeatmapsetVersionAsync(beatmapSetId, transaction);

            foreach (var file in parseResult.Files)
            {
                file.version_id = versionId;
                await db.InsertBeatmapsetVersionFileAsync(file, transaction);
            }

            await transaction.CommitAsync();
            // TODO: the ACID implications on this happening post-commit are... interesting... not sure anything can be done better?
            await beatmapStorage.StoreBeatmapSetAsync(beatmapSetId, await beatmapStream.ReadAllBytesToArrayAsync());
        }

        /// <summary>
        /// Upload a guest beatmap (difficulty) with the given beatmap ID to the set with the given ID
        /// </summary>
        /// <param name="beatmapSetId" example="241526">The ID of the beatmap set which the guest beatmap (difficulty) belongs to.</param>
        /// <param name="beatmapId" example="557814">The ID of the guest beatmap (difficulty) to update.</param>
        /// <param name="beatmapContents">The contents of the <c>.osu</c> file for the given guest beatmap (difficulty).</param>
        /// <response code="204">The guest beatmap (difficulty) has been successfully updated.</response>
        /// <response code="403">The user is not allowed to modify this beatmap (difficulty).</response>
        /// <response code="404">The request specified a beatmap (difficulty) that does not yet exist.</response>
        [HttpPatch]
        [Route("beatmapsets/{beatmapSetId}/beatmaps/{beatmapId}")]
        [ProducesResponseType(204)]
        public async Task<IActionResult> UploadGuestDifficultyAsync(
            [FromRoute] uint beatmapSetId,
            [FromRoute] uint beatmapId,
            IFormFile beatmapContents)
        {
            using var db = DatabaseAccess.GetConnection();

            var beatmap = await db.GetBeatmapAsync(beatmapSetId, beatmapId);
            if (beatmap == null)
                return NotFound();

            // TODO: ensure guest can't revive host's map (therefore using their quota)

            // TODO: revisit once https://github.com/ppy/osu-web/pull/11377 goes in
            if (beatmap.user_id != User.GetUserId())
                return Forbid();

            var archiveStream = await patcher.PatchBeatmapAsync(beatmapSetId, beatmapId, beatmapContents);
            await updateBeatmapSetFromArchiveAsync(beatmapSetId, archiveStream, db);
            return NoContent();
        }

        /// <summary>
        /// Download a historical version of a beatmap
        /// </summary>
        /// <remarks>
        /// NOTE: This endpoint is provisional and added for demonstrative purposes.
        /// It is not guaranteed that it will remain in this project going forward.
        /// </remarks>
        /// <param name="beatmapSetId" example="241526">The ID of the beatmap set to download.</param>
        /// <param name="versionId">The number of the version of the beatmap set to download.</param>
        /// <response code="200">The beatmap has been downloaded.</response>
        /// <response code="404">The request specified a beatmap set or version that does not yet exist.</response>
        [HttpGet]
        [AllowAnonymous]
        [Route("beatmapsets/{beatmapSetId}/versions/{versionId}")]
        [Produces("application/x-osu-beatmap-archive")]
        public async Task<IActionResult> DownloadBeatmapVersionAsync(
            [FromRoute] uint beatmapSetId,
            [FromRoute] uint versionId)
        {
            using var db = DatabaseAccess.GetConnection();

            (osu_beatmapset_version version, osu_beatmapset_version_file[] files)? versionInfo = await db.GetBeatmapsetVersionAsync(beatmapSetId, versionId);

            if (versionInfo == null)
                return NotFound();

            var archiveStream = await beatmapStorage.PackageBeatmapSetFilesAsync(versionInfo.Value.files);
            return File(archiveStream, "application/x-osu-beatmap-archive", $"{beatmapSetId}.v{versionId}.osz");
        }
    }
}
