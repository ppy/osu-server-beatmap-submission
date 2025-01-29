// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MySqlConnector;
using osu.Framework.Extensions;
using osu.Game.Beatmaps;
using osu.Game.IO.Archives;
using osu.Game.Rulesets.Objects;
using osu.Server.BeatmapSubmission.Authentication;
using osu.Server.BeatmapSubmission.Models;
using osu.Server.BeatmapSubmission.Models.API.Requests;
using osu.Server.BeatmapSubmission.Models.API.Responses;
using osu.Server.BeatmapSubmission.Models.Database;
using osu.Server.BeatmapSubmission.Services;
using osu.Server.QueueProcessor;
using StatsdClient;

namespace osu.Server.BeatmapSubmission
{
    [Authorize]
    public class BeatmapSubmissionController : Controller
    {
        private readonly ILogger<BeatmapSubmissionController> logger;
        private readonly IBeatmapStorage beatmapStorage;
        private readonly BeatmapPackagePatcher patcher;
        private readonly ILegacyIO legacyIO;
        private readonly IMirrorService mirrorService;

        public BeatmapSubmissionController(
            ILogger<BeatmapSubmissionController> logger,
            IBeatmapStorage beatmapStorage,
            BeatmapPackagePatcher patcher,
            ILegacyIO legacyIO,
            IMirrorService mirrorService)
        {
            this.logger = logger;
            this.beatmapStorage = beatmapStorage;
            this.patcher = patcher;
            this.legacyIO = legacyIO;
            this.mirrorService = mirrorService;
        }

        /// <summary>
        /// Create a new beatmap set, or add/remove beatmaps from an existing beatmap set
        /// </summary>
        /// <response code="200">The requested changes have been applied.</response>
        /// <response code="403">The user is not allowed to modify this beatmap set.</response>
        /// <response code="404">The request specified a beatmap set that does not yet exist.</response>
        /// <response code="422">The request was incorrectly formed, or could not be serviced due to violated invariants. Check returned error for details.</response>
        [HttpPut]
        [Route("beatmapsets")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(PutBeatmapSetResponse), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 422)]
        [EnableRateLimiting(Program.RATE_LIMIT_POLICY)]
        public async Task<IActionResult> PutBeatmapSetAsync([FromBody] PutBeatmapSetRequest request)
        {
            uint userId = User.GetUserId();

            using var db = await DatabaseAccess.GetConnectionAsync();

            await checkUserAccountStanding(db, userId);

            if (await db.GetUserMonthlyPlaycountAsync(userId) < 5)
                throw new InvariantException("Thanks for your contribution, but please play the game first!");

            uint? beatmapSetId = request.BeatmapSetID;
            uint[] existingBeatmaps = [];
            bool beatmapRevived = false;

            using var transaction = await db.BeginTransactionAsync();

            uint[] purgedMaps = await db.PurgeInactiveBeatmapSetsForUserAsync(userId, transaction);
            if (purgedMaps.Length > 0)
                logger.LogInformation("Purging inactive beatmap sets for user {userId} (ids: {purgedMaps})", userId, purgedMaps);

            (uint totalSlots, uint remainingSlots) = await getUploadQuota(db, userId, transaction);

            if (beatmapSetId == null)
            {
                if (request.BeatmapsToKeep.Length != 0)
                    throw new InvariantException("Cannot specify beatmaps to keep when creating a new beatmap set.", LogLevel.Warning);

                if (remainingSlots <= 0)
                {
                    throw new InvariantException($"You have exceeded your submission cap (you are currently allowed {totalSlots} total unranked maps). "
                                                 + $"Please finish the maps you currently have submitted, or wait until your submissions expire automatically to the graveyard "
                                                 + $"(about 4 weeks since last updated).");
                }

                string username = await db.GetUsernameAsync(userId, transaction);
                beatmapSetId = await db.CreateBlankBeatmapSetAsync(userId, username, transaction);
            }
            else
            {
                var beatmapSet = await db.GetBeatmapSetAsync(beatmapSetId.Value, transaction);

                if (beatmapSet == null)
                    return NotFound();

                if (beatmapSet.user_id != userId)
                {
                    logger.LogWarning("User {submitter} attempted to modify beatmap set {beatmapSetId} of user {owner}", userId, beatmapSetId, beatmapSet.user_id);
                    return Forbid();
                }

                if (beatmapSet.approved >= BeatmapOnlineStatus.Ranked)
                {
                    logger.LogWarning("User {submitter} attempted to modify ranked beatmap set {beatmapSetId}", userId, beatmapSetId);
                    return Forbid();
                }

                if (beatmapSet.approved == BeatmapOnlineStatus.Graveyard)
                {
                    logger.LogInformation("Reviving beatmap set {beatmapSetId}", beatmapSetId);
                    await reviveBeatmapSet(db, userId, beatmapSet, request.Target, transaction);
                    beatmapRevived = true;
                }

                existingBeatmaps = (await db.GetBeatmapIdsInSetAsync(beatmapSetId.Value, transaction)).ToArray();
            }

            if (request.BeatmapsToKeep.Except(existingBeatmaps).Any())
                throw new InvariantException("One of the beatmaps to keep does not belong to the specified set.", LogLevel.Warning);

            uint totalBeatmapCount = (uint)request.BeatmapsToKeep.Length + request.BeatmapsToCreate;

            if (totalBeatmapCount < 1)
                throw new InvariantException("The beatmap set must contain at least one beatmap.");

            if (totalBeatmapCount > 128)
                throw new InvariantException("The beatmap set cannot contain more than 128 beatmaps.");

            // C# enums suck, so this needs to be explicitly checked to prevent bad actors from doing "funny" stuff.
            if (!Enum.IsDefined(request.Target))
            {
                logger.LogWarning("User {submitter} attempted to send malicious parameters to change the ranked status of {beatmapSetId} to illegal value", userId, beatmapSetId);
                return Forbid();
            }

            IEnumerable<uint> beatmapsToDelete = existingBeatmaps.Except(request.BeatmapsToKeep).ToArray();

            if (beatmapsToDelete.Any())
            {
                logger.LogInformation("Deleting beatmaps {beatmapIds} in set {beatmapSetId}", beatmapsToDelete, beatmapSetId);
                foreach (uint beatmapId in beatmapsToDelete)
                    await db.DeleteBeatmapAsync(beatmapId, transaction);
            }

            var beatmapIds = new List<uint>();

            for (int i = 0; i < request.BeatmapsToCreate; ++i)
            {
                uint beatmapId = await db.CreateBlankBeatmapAsync(userId, beatmapSetId.Value, transaction);
                beatmapIds.Add(beatmapId);
            }

            if (beatmapIds.Count > 0)
                logger.LogInformation("Creating beatmaps {beatmapIds} in set {beatmapSetId}", beatmapIds, beatmapSetId);

            beatmapIds.AddRange(request.BeatmapsToKeep);

            await db.SetBeatmapSetOnlineStatusAsync(beatmapSetId.Value, (BeatmapOnlineStatus)request.Target, transaction);
            await db.UpdateBeatmapCountForSet(beatmapSetId.Value, totalBeatmapCount, transaction);

            await transaction.CommitAsync();

            if (beatmapRevived)
                await legacyIO.BroadcastReviveBeatmapSetEventAsync(beatmapSetId.Value);

            (beatmapset_version version, PackageFile[] files)? latestVersion = await db.GetLatestBeatmapsetVersionAsync(beatmapSetId.Value);

            return Ok(new PutBeatmapSetResponse
            {
                BeatmapSetId = beatmapSetId.Value,
                BeatmapIds = beatmapIds,
                Files = latestVersion?.files.Select(f => new BeatmapSetFile(f.VersionFile.filename, BitConverter.ToString(f.File.sha2_hash).Replace("-", ""))).ToArray() ?? [],
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
        /// <response code="422">The request was incorrectly formed, or could not be serviced due to violated invariants. Check returned error for details.</response>
        [HttpPut]
        [Route("beatmapsets/{beatmapSetId}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(typeof(ErrorResponse), 422)]
        [EnableRateLimiting(Program.RATE_LIMIT_POLICY)]
        public async Task<IActionResult> UploadFullPackageAsync(
            [FromRoute] uint beatmapSetId,
            // TODO: this won't fly on production, biggest existing beatmap archives exceed buffering limits (`MultipartBodyLengthLimit` = 128MB specifically)
            // potentially also https://github.com/aspnet/Announcements/issues/267
            // see: https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-8.0#small-and-large-files
            // needs further testing
            IFormFile beatmapArchive)
        {
            uint userId = User.GetUserId();

            using var db = await DatabaseAccess.GetConnectionAsync();

            await checkUserAccountStanding(db, userId);

            var beatmapSet = await db.GetBeatmapSetAsync(beatmapSetId);
            if (beatmapSet == null)
                return NotFound();

            if (beatmapSet.user_id != User.GetUserId())
            {
                logger.LogWarning("User {submitter} attempted to modify beatmap set {beatmapSetId} of user {owner}", userId, beatmapSetId, beatmapSet.user_id);
                return Forbid();
            }

            if (beatmapSet.approved >= BeatmapOnlineStatus.Ranked)
            {
                logger.LogWarning("User {submitter} attempted to modify ranked beatmap set {beatmapSetId}", userId, beatmapSetId);
                return Forbid();
            }

            if (beatmapSet.approved == BeatmapOnlineStatus.Graveyard)
                throw new InvariantException("The beatmap set must be revived first.", LogLevel.Warning);

            using var beatmapStream = beatmapArchive.OpenReadStream();

            // this endpoint should always be used on a first submission,
            // but do a check which matches stable to make sure that it is the case.
            bool newSubmission = beatmapSet.bpm == 0;

            if (await updateBeatmapSetFromArchiveAsync(beatmapSetId, beatmapStream, db))
            {
                if (newSubmission)
                    await legacyIO.BroadcastNewBeatmapSetEventAsync(beatmapSetId);
                else
                    await legacyIO.BroadcastUpdateBeatmapSetEventAsync(beatmapSetId, userId);
            }

            DogStatsd.Increment("submissions_completed", tags: ["full"]);
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
        /// <response code="422">The request was incorrectly formed, or could not be serviced due to violated invariants. Check returned error for details.</response>
        [HttpPatch]
        [Route("beatmapsets/{beatmapSetId}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(typeof(ErrorResponse), 422)]
        [EnableRateLimiting(Program.RATE_LIMIT_POLICY)]
        public async Task<IActionResult> PatchPackageAsync(
            [FromRoute] uint beatmapSetId,
            IFormFileCollection filesChanged,
            [FromForm] string[] filesDeleted)
        {
            uint userId = User.GetUserId();
            using var db = await DatabaseAccess.GetConnectionAsync();

            await checkUserAccountStanding(db, userId);

            var beatmapSet = await db.GetBeatmapSetAsync(beatmapSetId);
            if (beatmapSet == null)
                return NotFound();

            if (beatmapSet.user_id != User.GetUserId())
            {
                logger.LogWarning("User {submitter} attempted to modify beatmap set {beatmapSetId} of user {owner}", userId, beatmapSetId, beatmapSet.user_id);
                return Forbid();
            }

            if (beatmapSet.approved >= BeatmapOnlineStatus.Ranked)
            {
                logger.LogWarning("User {submitter} attempted to modify ranked beatmap set {beatmapSetId}", userId, beatmapSetId);
                return Forbid();
            }

            if (await db.GetLatestBeatmapsetVersionAsync(beatmapSetId) == null)
                return NotFound();

            if (beatmapSet.approved == BeatmapOnlineStatus.Graveyard)
                throw new InvariantException("The beatmap set must be revived first.", LogLevel.Warning);

            if (filesChanged.Any(f => SanityCheckHelpers.IncursPathTraversalRisk(f.FileName)))
                throw new InvariantException("Invalid filename detected", LogLevel.Warning);

            var beatmapStream = await patcher.PatchBeatmapSetAsync(beatmapSetId, filesChanged, filesDeleted);

            if (await updateBeatmapSetFromArchiveAsync(beatmapSetId, beatmapStream, db))
                await legacyIO.BroadcastUpdateBeatmapSetEventAsync(beatmapSetId, userId);

            DogStatsd.Increment("submissions_completed", tags: ["update"]);
            return NoContent();
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
        [ProducesResponseType(typeof(ErrorResponse), 422)]
        [EnableRateLimiting(Program.RATE_LIMIT_POLICY)]
        public async Task<IActionResult> UploadGuestDifficultyAsync(
            [FromRoute] uint beatmapSetId,
            [FromRoute] uint beatmapId,
            IFormFile beatmapContents)
        {
            uint userId = User.GetUserId();
            using var db = await DatabaseAccess.GetConnectionAsync();

            await checkUserAccountStanding(db, userId);

            var beatmapSet = await db.GetBeatmapSetAsync(beatmapSetId);
            var beatmap = await db.GetBeatmapAsync(beatmapSetId, beatmapId);
            if (beatmapSet == null || beatmap == null)
                return NotFound();

            if (beatmapSet.approved >= BeatmapOnlineStatus.Ranked)
            {
                logger.LogWarning("User {submitter} attempted to modify ranked beatmap set {beatmapSetId}", userId, beatmapSetId);
                return Forbid();
            }

            IEnumerable<uint> beatmapOwners = await db.GetBeatmapOwnersAsync(beatmap.beatmap_id);

            if (beatmap.user_id != userId && !beatmapOwners.Contains(userId))
            {
                logger.LogWarning("User {submitter} attempted to modify beatmap {beatmapId} in set {beatmapSetId} they are not owner of", userId, beatmapId, beatmapSetId);
                return Forbid();
            }

            if (await db.GetLatestBeatmapsetVersionAsync(beatmapSetId) == null)
                return NotFound();

            if (beatmapSet.approved == BeatmapOnlineStatus.Graveyard)
                throw new InvariantException("The beatmap set is in the graveyard. Please ask the set owner to revive it first.", LogLevel.Warning);

            if (SanityCheckHelpers.IncursPathTraversalRisk(beatmapContents.FileName))
                throw new InvariantException("Invalid filename detected", LogLevel.Warning);

            var archiveStream = await patcher.PatchBeatmapAsync(beatmapSetId, beatmap, beatmapContents);

            if (await updateBeatmapSetFromArchiveAsync(beatmapSetId, archiveStream, db))
                await legacyIO.BroadcastUpdateBeatmapSetEventAsync(beatmapSetId, userId);

            DogStatsd.Increment("submissions_completed", tags: ["guest"]);
            return NoContent();
        }

        private static async Task checkUserAccountStanding(MySqlConnection connection, uint userId, MySqlTransaction? transaction = null)
        {
            if (await connection.IsUserRestrictedAsync(userId, transaction))
                throw new InvariantException("Your account is currently restricted.");

            if (await connection.IsUserSilencedAsync(userId, transaction))
                throw new InvariantException("You are unable to submit or update maps while silenced.");
        }

        private static async Task<(uint totalSlots, uint remainingSlots)> getUploadQuota(MySqlConnection connection, uint userId, MySqlTransaction? transaction = null)
        {
            (uint unrankedCount, uint rankedCount) = await connection.GetUserBeatmapSetCountAsync(userId, transaction);
            uint quota = await connection.IsUserSupporterAsync(userId, transaction)
                ? 8 + Math.Min(12, rankedCount)
                : 4 + Math.Min(4, rankedCount);
            return (quota, (uint)Math.Max((int)quota - (int)unrankedCount, 0));
        }

        private static async Task reviveBeatmapSet(MySqlConnection connection, uint userId, osu_beatmapset beatmapSet, BeatmapSubmissionTarget requestTarget, MySqlTransaction? transaction = null)
        {
            if (beatmapSet.download_disabled > 0)
                throw new InvariantException("Could not perform this action. Please send an e-mail to support@ppy.sh to follow up on this.");

            (_, uint remainingSlots) = await getUploadQuota(connection, userId, transaction);
            if (remainingSlots <= 0)
                throw new InvariantException("Beatmap is in the graveyard and you don't have enough remaining upload quota to resurrect it.");

            await connection.SetBeatmapSetOnlineStatusAsync(beatmapSet.beatmapset_id, (BeatmapOnlineStatus)requestTarget, transaction);
        }

        /// <summary>
        /// Performs all relevant update operations on a beatmap set, given a stream with the full contents of its package.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if any change was actually performed.
        /// <see langword="false"/> if the supplied package is functionally equivalent to the previous version and as such there is nothing to be done.
        /// </returns>
        private async Task<bool> updateBeatmapSetFromArchiveAsync(uint beatmapSetId, Stream beatmapStream, MySqlConnection db)
        {
            using var archiveReader = new ZipArchiveReader(beatmapStream);
            var parseResult = BeatmapPackageParser.Parse(beatmapSetId, archiveReader);

            checkPackageSize(beatmapStream.Length, parseResult);

            using var transaction = await db.BeginTransactionAsync();

            HashSet<uint> beatmapIds = (await db.GetBeatmapIdsInSetAsync(beatmapSetId, transaction)).ToHashSet();

            (beatmapset_version, PackageFile[] files)? lastVersion = await db.GetLatestBeatmapsetVersionAsync(beatmapSetId, transaction);

            if (lastVersion != null)
            {
                HashSet<PackageFile> fileSet = lastVersion.Value.files.ToHashSet(new PackageFileEqualityComparer());

                if (fileSet.SetEquals(parseResult.Files))
                {
                    await transaction.RollbackAsync();
                    return false;
                }
            }

            foreach (var versionedFile in parseResult.Files)
                versionedFile.VersionFile.file_id = await db.InsertBeatmapsetFileAsync(versionedFile.File, transaction);

            ulong versionId = await db.CreateBeatmapsetVersionAsync(beatmapSetId, transaction);

            foreach (var packageFile in parseResult.Files)
            {
                packageFile.VersionFile.version_id = versionId;
                await db.InsertBeatmapsetVersionFileAsync(packageFile.VersionFile, transaction);

                if (packageFile.BeatmapContent is BeatmapContent content)
                {
                    if (!beatmapIds.Remove((uint)content.Beatmap.BeatmapInfo.OnlineID))
                        throw new InvariantException($"Beatmap has invalid ID inside ({packageFile.VersionFile.filename}).", LogLevel.Warning);

                    await db.UpdateBeatmapAsync(content.GetDatabaseRow(), transaction);
                }
            }

            if (beatmapIds.Count > 0)
                throw new InvariantException($"Beatmap package is missing .osu files for beatmaps with IDs: {string.Join(", ", beatmapIds)}", LogLevel.Warning);

            await db.UpdateBeatmapSetAsync(parseResult.BeatmapSet, transaction);

            await transaction.CommitAsync();

            // TODO: the ACID implications on this happening post-commit are... interesting... not sure anything can be done better?
            await beatmapStorage.StoreBeatmapSetAsync(beatmapSetId, await beatmapStream.ReadAllBytesToArrayAsync(), parseResult);

            if (await db.IsBeatmapSetNominatedAsync(beatmapSetId))
                await legacyIO.DisqualifyBeatmapSetAsync(beatmapSetId, "This beatmap set was updated by the mapper after a nomination. Please ensure to re-check the beatmaps for new issues. If you are the mapper, please comment in this thread on what you changed.");

            await mirrorService.PurgeBeatmapSetAsync(db, beatmapSetId);

            if (!await db.IsBeatmapSetInProcessingQueueAsync(beatmapSetId))
            {
                await db.AddBeatmapSetToProcessingQueueAsync(beatmapSetId);
                await legacyIO.IndexBeatmapSetAsync(beatmapSetId);
            }

            await legacyIO.RefreshBeatmapSetCacheAsync(beatmapSetId);
            return true;
        }

        private void checkPackageSize(long packageSizeBytes, BeatmapPackageParseResult packageParseResult)
        {
            const long minimum_package_size_bytes = 5 * 1024 * 1024; // 5MB
            const long max_bytes_per_second = 166667; // 10MB / minute

            double longestBeatmapLengthMilliseconds = 0;

            foreach (var file in packageParseResult.Files)
            {
                if (file.BeatmapContent is not BeatmapContent content)
                    continue;

                if (content.Beatmap.HitObjects.Count == 0)
                    continue;

                longestBeatmapLengthMilliseconds = Math.Max(longestBeatmapLengthMilliseconds, content.Beatmap.HitObjects.Max(ho => ho.GetEndTime()));
            }

            long allowableSizeBytes = minimum_package_size_bytes + (long)(longestBeatmapLengthMilliseconds / 1000 * max_bytes_per_second);

            if (packageSizeBytes > allowableSizeBytes)
            {
                throw new InvariantException($"The beatmap package is too large. "
                                             + $"The size of the package with the requested changes applied is {humaniseSize(packageSizeBytes)}. "
                                             + $"The maximum allowable size is {humaniseSize(allowableSizeBytes)}.");
            }

            static string humaniseSize(double sizeBytes)
            {
                string humanisedSize;

                if (sizeBytes < 1024)
                    humanisedSize = $@"{sizeBytes}B";
                else if (sizeBytes < 1024 * 1024)
                    humanisedSize = $@"{sizeBytes / 1024:#.0}kB";
                else
                    humanisedSize = $@"{sizeBytes / 1024 / 1024:#.0}MB";

                return humanisedSize;
            }
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
            using var db = await DatabaseAccess.GetConnectionAsync();

            (beatmapset_version version, PackageFile[] files)? versionInfo = await db.GetBeatmapsetVersionAsync(beatmapSetId, versionId);

            if (versionInfo == null)
                return NotFound();

            var archiveStream = await beatmapStorage.PackageBeatmapSetFilesAsync(versionInfo.Value.files);
            return File(archiveStream, "application/x-osu-beatmap-archive", $"{beatmapSetId}.v{versionId}.osz");
        }
    }
}
