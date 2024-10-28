// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
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
using osu.Server.QueueProcessor;

namespace osu.Server.BeatmapSubmission
{
    public class BeatmapSubmissionController : Controller
    {
        [HttpPut]
        [Route("beatmapsets")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public async Task<CreateBeatmapSetResponse> CreateBeatmapSetAsync([FromBody] CreateBeatmapSetRequest request)
        {
            // TODO: do all of the due diligence checks

            using var db = DatabaseAccess.GetConnection();
            using var transaction = await db.BeginTransactionAsync();

            string username = await db.QuerySingleAsync<string>(@"SELECT `username` FROM `phpbb_users` WHERE `user_id` = @userId",
                new
                {
                    userId = User.GetUserId(),
                },
                transaction);

            uint beatmapSetId = await db.QuerySingleAsync<uint>(
                """
                INSERT INTO `osu_beatmapsets`
                    (`user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`)
                VALUES
                    (@userId, @creator, -1, 0, -1, CURRENT_TIMESTAMP);
                    
                SELECT LAST_INSERT_ID();
                """,
                new
                {
                    userId = User.GetUserId(),
                    creator = username,
                },
                transaction);

            var beatmapIds = new List<uint>((int)request.BeatmapCount);

            for (int i = 0; i < request.BeatmapCount; ++i)
            {
                uint beatmapId = await db.QuerySingleAsync<uint>(
                    """
                    INSERT INTO `osu_beatmaps`
                        (`user_id`, `beatmapset_id`, `approved`)
                    VALUES
                        (@userId, @beatmapSetId, -1);
                        
                    SELECT LAST_INSERT_ID();
                    """,
                    new
                    {
                        userId = User.GetUserId(),
                        beatmapSetId = beatmapSetId,
                    },
                    transaction);
                beatmapIds.Add(beatmapId);
            }

            await transaction.CommitAsync();

            return new CreateBeatmapSetResponse
            {
                BeatmapSetId = beatmapSetId,
                BeatmapIds = beatmapIds
            };
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
            // TODO: do proper differential updates on a beatmap level
            // TODO: do all of the due diligence checks

            using var beatmapStream = beatmapArchive.OpenReadStream();
            using var archiveReader = new ZipArchiveReader(beatmapStream);

            string[] filenames = archiveReader.Filenames.ToArray();

            BeatmapContent[] beatmaps = getBeatmapContent(archiveReader, filenames).ToArray();

            osu_beatmap[] beatmapRows = beatmaps.Select(constructDatabaseRowForBeatmap).ToArray();
            var beatmapSetRow = constructDatabaseRowForBeatmapset(beatmapSetId, archiveReader, beatmaps);

            using var db = DatabaseAccess.GetConnection();
            using var transaction = await db.BeginTransactionAsync();

            foreach (var beatmapRow in beatmapRows)
            {
                await db.ExecuteAsync(
                    """
                    UPDATE `osu_beatmaps`
                    SET
                        `last_update` = CURRENT_TIMESTAMP,
                        `filename` = @filename, `checksum` = @checksum, `version` = @version,
                        `diff_drain` = @diff_drain, `diff_size` = @diff_size, `diff_overall` = @diff_overall, `diff_approach` = @diff_approach,
                        `total_length` = @total_length, `hit_length` = @hit_length,
                        `playcount` = 0, `passcount` = 0,
                        `countTotal` = @countTotal, `countNormal` = @countNormal, `countSlider` = @countSlider, `countSpinner` = @countSpinner,
                        `playmode` = @playmode
                    WHERE `beatmap_id` = @beatmap_id AND `deleted_at` IS NULL
                    """,
                    beatmapRow,
                    transaction);
            }

            await db.ExecuteAsync(
                """
                UPDATE `osu_beatmapsets`
                SET
                    `artist` = @artist, `artist_unicode` = @artist_unicode, `title` = @title, `title_unicode` = @title_unicode,
                    `source` = @source, `creator` = @creator, `tags` = @tags, `video` = @video, `storyboard` = @storyboard,
                    `storyboard_hash` = @storyboard_hash, `bpm` = @bpm, `filename` = @filename, `displaytitle` = @displaytitle,
                    `body_hash` = NULL, `header_hash` = NULL, `osz2_hash` = NULL, `active` = 1, `last_update` = CURRENT_TIMESTAMP
                WHERE `beatmapset_id` = @beatmapset_id
                """,
                beatmapSetRow,
                transaction);

            await transaction.CommitAsync();
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
                bpm = (float)(60000 / beatmaps.First().Beatmap.GetMostCommonBeatLength()),
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
