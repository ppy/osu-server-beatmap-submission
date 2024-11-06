// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using MySqlConnector;
using osu.Server.BeatmapSubmission.Models;

namespace osu.Server.BeatmapSubmission
{
    public static class DatabaseOperationExtensions
    {
        public static Task<string> GetUsernameAsync(this MySqlConnection db, uint userId, MySqlTransaction? transaction = null)
        {
            return db.QuerySingleAsync<string>(@"SELECT `username` FROM `phpbb_users` WHERE `user_id` = @userId",
                new
                {
                    userId = userId,
                },
                transaction);
        }

        public static Task<osu_beatmapset?> GetBeatmapSetAsync(this MySqlConnection db, uint beatmapSetId, MySqlTransaction? transaction = null)
        {
            return db.QuerySingleOrDefaultAsync<osu_beatmapset?>(@"SELECT * FROM `osu_beatmapsets` WHERE `beatmapset_id` = @beatmapSetId",
                new
                {
                    beatmapSetId = beatmapSetId,
                },
                transaction);
        }

        public static Task<IEnumerable<uint>> GetBeatmapIdsInSetAsync(this MySqlConnection db, uint beatmapSetId, MySqlTransaction? transaction = null)
        {
            return db.QueryAsync<uint>(@"SELECT `beatmap_id` FROM `osu_beatmaps` WHERE `beatmapset_id` = @beatmapSetId",
                new
                {
                    beatmapSetId = beatmapSetId,
                },
                transaction);
        }

        public static Task<osu_beatmap?> GetBeatmapAsync(this MySqlConnection db, uint beatmapSetId, uint beatmapId, MySqlTransaction? transaction = null)
        {
            return db.QuerySingleOrDefaultAsync<osu_beatmap?>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` = @beatmapId AND `beatmapset_id` = @beatmapSetId",
                new
                {
                    beatmapSetId = beatmapSetId,
                    beatmapId = beatmapId,
                },
                transaction);
        }

        public static Task<uint> CreateBlankBeatmapSetAsync(this MySqlConnection db, uint userId, string username, MySqlTransaction? transaction = null)
        {
            return db.QuerySingleAsync<uint>(
                """
                INSERT INTO `osu_beatmapsets`
                    (`user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`)
                VALUES
                    (@userId, @creator, -1, 0, -1, CURRENT_TIMESTAMP);
                    
                SELECT LAST_INSERT_ID();
                """,
                new
                {
                    userId = userId,
                    creator = username,
                },
                transaction);
        }

        public static Task<uint> CreateBlankBeatmapAsync(this MySqlConnection db, uint userId, uint beatmapSetId, MySqlTransaction? transaction = null)
        {
            return db.QuerySingleAsync<uint>(
                """
                INSERT INTO `osu_beatmaps`
                    (`user_id`, `beatmapset_id`, `approved`)
                VALUES
                    (@userId, @beatmapSetId, -1);
                    
                SELECT LAST_INSERT_ID();
                """,
                new
                {
                    userId = userId,
                    beatmapSetId = beatmapSetId,
                },
                transaction);
        }

        public static Task DeleteBeatmapAsync(this MySqlConnection db, uint beatmapId, MySqlTransaction? transaction = null)
        {
            return db.ExecuteAsync("UPDATE `osu_beatmaps` SET `deleted_at` = NOW() WHERE `beatmap_id` = @beatmapId",
                new
                {
                    beatmapId = beatmapId
                },
                transaction);
        }

        public static Task UpdateBeatmapAsync(this MySqlConnection db, osu_beatmap beatmap, MySqlTransaction? transaction = null)
        {
            return db.ExecuteAsync(
                """
                UPDATE `osu_beatmaps`
                SET
                    `last_update` = CURRENT_TIMESTAMP,
                    `filename` = @filename, `checksum` = @checksum, `version` = @version,
                    `diff_drain` = @diff_drain, `diff_size` = @diff_size, `diff_overall` = @diff_overall, `diff_approach` = @diff_approach,
                    `total_length` = @total_length, `hit_length` = @hit_length, `bpm` = @bpm,
                    `playcount` = 0, `passcount` = 0,
                    `countTotal` = @countTotal, `countNormal` = @countNormal, `countSlider` = @countSlider, `countSpinner` = @countSpinner,
                    `playmode` = @playmode
                WHERE `beatmap_id` = @beatmap_id AND `deleted_at` IS NULL
                """,
                beatmap,
                transaction);
        }

        public static Task UpdateBeatmapSetAsync(this MySqlConnection db, osu_beatmapset beatmapSet, MySqlTransaction? transaction = null)
        {
            return db.ExecuteAsync(
                """
                UPDATE `osu_beatmapsets`
                SET
                    `artist` = @artist, `artist_unicode` = @artist_unicode, `title` = @title, `title_unicode` = @title_unicode,
                    `source` = @source, `tags` = @tags, `video` = @video, `storyboard` = @storyboard,
                    `storyboard_hash` = @storyboard_hash, `bpm` = @bpm, `filename` = @filename, `displaytitle` = @displaytitle,
                    `body_hash` = NULL, `header_hash` = NULL, `osz2_hash` = NULL, `active` = 1, `last_update` = CURRENT_TIMESTAMP
                WHERE `beatmapset_id` = @beatmapset_id
                """,
                beatmapSet,
                transaction);
        }
    }
}
