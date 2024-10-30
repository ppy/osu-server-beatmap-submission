// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using osu.Framework.Extensions;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.IO.Archives;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Storyboards;
using osu.Server.BeatmapSubmission.Authentication;
using osu.Server.BeatmapSubmission.Models;
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

        public BeatmapSubmissionController(IBeatmapStorage beatmapStorage)
        {
            this.beatmapStorage = beatmapStorage;
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

                // commentary for later:
                // this is going to block guest difficulties a bit.
                // guest difficulty updating is going to need to be a separate operation
                // and client will need to keep appropriate metadata to know what it wants to do (upload new set vs update guest diff).
                if (beatmapSet.user_id != userId)
                    return Forbid();

                existingBeatmaps = (await db.GetBeatmapIdsInSetAsync(beatmapSetId.Value, transaction)).ToArray();
            }

            if (request.BeatmapsToKeep.Except(existingBeatmaps).Any())
                return UnprocessableEntity("One of the beatmaps to keep does not belong to the specified set.");

            foreach (uint beatmapId in existingBeatmaps.Except(request.BeatmapsToKeep))
                await db.DeleteBeatmapAsync(beatmapId, transaction);

            var beatmapIds = new List<uint>((int)request.BeatmapsToCreate);

            for (int i = 0; i < request.BeatmapsToCreate; ++i)
            {
                uint beatmapId = await db.CreateBlankBeatmapAsync(userId, beatmapSetId.Value, transaction);
                beatmapIds.Add(beatmapId);
            }

            await transaction.CommitAsync();

            return Ok(new PutBeatmapSetResponse
            {
                BeatmapSetId = beatmapSetId.Value,
                BeatmapIds = beatmapIds
            });
        }

        [HttpPut]
        [Route("beatmapsets/{beatmapSetId}")]
        public async Task ReplaceBeatmapSetAsync(
            [FromRoute] uint beatmapSetId,
            // TODO: this won't fly on production, biggest existing beatmap archives exceed buffering limits.
            // see: https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-8.0#small-and-large-files
            // using this for now just to get something going.
            [FromForm] IFormFile beatmapArchive)
        {
            // TODO: do all of the due diligence checks

            using var beatmapStream = beatmapArchive.OpenReadStream();
            using var archiveReader = new ZipArchiveReader(beatmapStream);

            string[] filenames = archiveReader.Filenames.ToArray();

            BeatmapContent[] beatmaps = getBeatmapContent(archiveReader, filenames).ToArray();

            osu_beatmap[] beatmapRows = beatmaps.Select(constructDatabaseRowForBeatmap).ToArray();
            var beatmapSetRow = constructDatabaseRowForBeatmapset(beatmapSetId, archiveReader, beatmaps);

            using var db = DatabaseAccess.GetConnection();
            using var transaction = await db.BeginTransactionAsync();

            // TODO: ensure these actually belong to the beatmap set
            foreach (var beatmapRow in beatmapRows)
                await db.UpdateBeatmapAsync(beatmapRow, transaction);

            await db.UpdateBeatmapSetAsync(beatmapSetRow, transaction);

            await transaction.CommitAsync();
            await beatmapStorage.StoreBeatmapSetAsync(beatmapSetId, await beatmapStream.ReadAllBytesToArrayAsync());
        }

        private static IEnumerable<BeatmapContent> getBeatmapContent(ZipArchiveReader archiveReader, string[] filenames)
        {
            foreach (string file in filenames.Where(filename => Path.GetExtension(filename).Equals(".osu", StringComparison.OrdinalIgnoreCase)))
            {
                using var contents = archiveReader.GetStream(file);
                var decoder = new LegacyBeatmapDecoder();
                var beatmap = decoder.Decode(new LineBufferedReader(contents));

                yield return new BeatmapContent(Path.GetFileName(file), contents.ComputeMD5Hash(), beatmap);
            }
        }

        private record BeatmapContent(string Filename, string MD5, Beatmap Beatmap);

        private static osu_beatmap constructDatabaseRowForBeatmap(BeatmapContent beatmapContent)
        {
            float beatLength = (float)beatmapContent.Beatmap.GetMostCommonBeatLength();

            var result = new osu_beatmap
            {
                beatmap_id = (uint)beatmapContent.Beatmap.BeatmapInfo.OnlineID,
                filename = beatmapContent.Filename,
                checksum = beatmapContent.MD5,
                version = beatmapContent.Beatmap.BeatmapInfo.DifficultyName,
                diff_drain = beatmapContent.Beatmap.Difficulty.DrainRate,
                diff_size = beatmapContent.Beatmap.Difficulty.CircleSize,
                diff_overall = beatmapContent.Beatmap.Difficulty.OverallDifficulty,
                diff_approach = beatmapContent.Beatmap.Difficulty.ApproachRate,
                bpm = beatLength > 0 ? 60000 / beatLength : 0,
                total_length = (uint)(beatmapContent.Beatmap.CalculatePlayableLength() / 1000),
                hit_length = (uint)(beatmapContent.Beatmap.CalculateDrainLength() / 1000),
                playmode = (ushort)beatmapContent.Beatmap.BeatmapInfo.Ruleset.OnlineID,
            };

            countObjectsByType(beatmapContent.Beatmap, result);
            return result;
        }

        // didn't want to incur conversion overheads here just for this, so here you go.
        // TODO: decide if this is ok or not
        private static void countObjectsByType(Beatmap beatmap, osu_beatmap result)
        {
            foreach (var hitobject in beatmap.HitObjects)
            {
                switch (result.playmode)
                {
                    case 0:
                    case 2:
                    {
                        switch (hitobject)
                        {
                            case IHasPathWithRepeats:
                                result.countSlider += 1;
                                break;

                            case IHasDuration:
                                result.countSpinner += 1;
                                break;

                            default:
                                result.countNormal += 1;
                                break;
                        }

                        break;
                    }

                    case 1:
                    {
                        switch (hitobject)
                        {
                            case IHasPath:
                                result.countSlider += 1;
                                break;

                            case IHasDuration:
                                result.countSpinner += 1;
                                break;

                            default:
                                result.countNormal += 1;
                                break;
                        }

                        break;
                    }

                    case 3:
                    {
                        switch (hitobject)
                        {
                            case IHasDuration:
                                result.countSlider += 1;
                                break;

                            default:
                                result.countNormal += 1;
                                break;
                        }

                        break;
                    }
                }

                result.countTotal += 1;
            }
        }

        private static osu_beatmapset constructDatabaseRowForBeatmapset(uint beatmapSetId, ZipArchiveReader archiveReader, BeatmapContent[] beatmaps)
        {
            if (beatmaps.Length == 0)
                throw new InvalidOperationException("The uploaded beatmap set must have at least one difficulty.");

            T getSingleValueFrom<T>(IEnumerable<BeatmapContent> beatmapContents, Func<BeatmapContent, T> accessor, string valueName)
            {
                T[] distinctValues = beatmapContents.Select(accessor).Distinct().ToArray();
                if (distinctValues.Length != 1)
                    throw new InvalidOperationException($"The uploaded beatmap set's individual difficulties have inconsistent {valueName}. Please unify {valueName} before re-submitting.");

                return distinctValues.Single();
            }

            float firstBeatLength = (float)beatmaps.First().Beatmap.GetMostCommonBeatLength();

            var result = new osu_beatmapset
            {
                beatmapset_id = beatmapSetId, // TODO: actually verify these
                artist = getSingleValueFrom(beatmaps, c => c.Beatmap.Metadata.Artist, nameof(BeatmapMetadata.Artist)),
                artist_unicode = getSingleValueFrom(beatmaps,
                    c => !string.IsNullOrEmpty(c.Beatmap.Metadata.ArtistUnicode) ? c.Beatmap.Metadata.ArtistUnicode : null,
                    nameof(BeatmapMetadata.ArtistUnicode)),
                title = getSingleValueFrom(beatmaps, c => c.Beatmap.Metadata.Title, nameof(BeatmapMetadata.Title)),
                title_unicode = getSingleValueFrom(beatmaps,
                    c => !string.IsNullOrEmpty(c.Beatmap.Metadata.TitleUnicode) ? c.Beatmap.Metadata.TitleUnicode : null,
                    nameof(BeatmapMetadata.TitleUnicode)),
                source = getSingleValueFrom(beatmaps, c => c.Beatmap.Metadata.Source, nameof(BeatmapMetadata.Source)),
                tags = getSingleValueFrom(beatmaps, c => c.Beatmap.Metadata.Tags, nameof(BeatmapMetadata.Tags)),
                bpm = firstBeatLength > 0 ? 60000 / firstBeatLength : 0,
                filename = FormattableString.Invariant($"{beatmapSetId}.osz"),
            };

            // TODO: maybe legacy cruft?
            result.displaytitle = $"[bold:0,size:20]{result.artist_unicode}|{result.title_unicode}";

            // TODO: this is not doing `difficulty_names` because i'm *relatively* sure that cannot work correctly in the original BSS either
            // (old BSS embeds star ratings into the value of that which are not going to be correct now).

            if (archiveReader.Filenames.Any(f => OsuGameBase.VIDEO_EXTENSIONS.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                result.video = true;
            }

            if (archiveReader.Filenames.FirstOrDefault(f => Path.GetExtension(f).Equals(".osb", StringComparison.OrdinalIgnoreCase)) is string storyboardFileName)
            {
                using var storyboardStream = archiveReader.GetStream(storyboardFileName);
                var storyboardDecoder = new LegacyStoryboardDecoder();
                var storyboard = storyboardDecoder.Decode(new LineBufferedReader(storyboardStream));

                if (storyboard.Layers.Any(l => l.Elements.Any(elem => elem.GetType() == typeof(StoryboardSprite) || elem.GetType() == typeof(StoryboardAnimation))))
                {
                    result.storyboard = true;
                    result.storyboard_hash = storyboardStream.ComputeMD5Hash();
                }
            }

            return result;
        }
    }
}
