// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using MySqlConnector;
using osu.Server.BeatmapSubmission.Models;
using osu.Server.BeatmapSubmission.Models.Database;

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

        public static async Task<ulong> InsertBeatmapsetFileAsync(this MySqlConnection db, osu_beatmapset_file file, MySqlTransaction? transaction = null)
        {
            var existing = await db.QuerySingleOrDefaultAsync<osu_beatmapset_file?>(
                "SELECT * FROM `osu_beatmapset_files` WHERE `sha2_hash` = @sha2_hash",
                file,
                transaction);

            if (existing != null)
            {
                if (existing.file_size != file.file_size)
                    throw new InvalidOperationException("There is already a file in the database with the same SHA2 but different size. This is very very VERY bad news.");

                return file.file_id = existing.file_id;
            }

            return file.file_id = await db.QuerySingleAsync<ulong>(
                "INSERT INTO `osu_beatmapset_files` (`sha2_hash`, `file_size`) VALUES (@sha2_hash, @file_size) AS `new`; SELECT LAST_INSERT_ID();",
                file,
                transaction);
        }

        public static async Task<(osu_beatmapset_version, VersionedFile[])?> GetBeatmapsetVersionAsync(this MySqlConnection db, uint beatmapSetId, ulong versionId, MySqlTransaction? transaction = null)
        {
            var version = await db.QuerySingleOrDefaultAsync<osu_beatmapset_version?>(
                "SELECT * FROM `osu_beatmapset_versions` WHERE `beatmapset_id` = @beatmapset_id AND `version_id` = @version_id",
                new
                {
                    beatmapset_id = beatmapSetId,
                    version_id = versionId
                });

            if (version == null)
                return null;

            VersionedFile[] files = (await db.QueryAsync(
                """
                SELECT `f`.`file_id`, `f`.`sha2_hash`, `f`.`file_size`, `vf`.`file_id` AS `versioned_file_id`, `vf`.`version_id`, `vf`.`filename` FROM `osu_beatmapset_files` `f`
                JOIN `osu_beatmapset_version_files` `vf` ON `f`.`file_id` = `vf`.`file_id`
                WHERE `vf`.`version_id` = @version_id
                """,
                (osu_beatmapset_file file, osu_beatmapset_version_file versionFile) => new VersionedFile(file, versionFile),
                new
                {
                    version_id = version.version_id
                },
                transaction,
                splitOn: "versioned_file_id")).ToArray();

            return (version, files);
        }

        public static async Task<ulong> CreateBeatmapsetVersionAsync(this MySqlConnection db, uint beatmapSetId, MySqlTransaction? transaction = null)
        {
            ulong? previousVersion = await db.QuerySingleAsync<ulong?>(
                "SELECT MAX(`version_id`) FROM `osu_beatmapset_versions` WHERE `beatmapset_id` = @beatmapset_id",
                new
                {
                    beatmapset_id = beatmapSetId
                },
                transaction);

            return await db.QuerySingleAsync<ulong>(
                "INSERT INTO `osu_beatmapset_versions` (`beatmapset_id`, `previous_version_id`) VALUES (@beatmapset_id, @previous_version_id); SELECT LAST_INSERT_ID();",
                new
                {
                    beatmapset_id = beatmapSetId,
                    previous_version_id = previousVersion,
                },
                transaction);
        }

        public static Task InsertBeatmapsetVersionFileAsync(this MySqlConnection db, osu_beatmapset_version_file versionFile, MySqlTransaction? transaction = null)
        {
            return db.ExecuteAsync(
                "INSERT INTO `osu_beatmapset_version_files` (`file_id`, `version_id`, `filename`) VALUES (@file_id, @version_id, @filename)",
                versionFile,
                transaction);
        }
    }
}
