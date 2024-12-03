// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using MySqlConnector;
using osu.Game.Beatmaps;
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

        /// <seealso href="https://github.com/ppy/osu-web/blob/1fbc73baa8e8be6759b1cc4f4ab509d8ae53a165/app/Models/User.php#L1099-L1102"><c>isBanned()</c> in osu-web</seealso>
        /// <seealso href="https://github.com/ppy/osu-web/blob/1fbc73baa8e8be6759b1cc4f4ab509d8ae53a165/app/Models/User.php#L1109-L1112"><c>isRestricted()</c> in osu-web</seealso>
        public static async Task<bool> IsUserRestrictedAsync(this MySqlConnection db, uint userId, MySqlTransaction? transaction = null)
        {
            var standing = await db.QuerySingleAsync<(short user_type, short user_warnings)>(
                @"SELECT `user_type`, `user_warnings` FROM `phpbb_users` WHERE `user_id` = @user_id",
                new
                {
                    user_id = userId,
                },
                transaction);

            return standing.user_type == 1 || standing.user_warnings > 0;
        }

        /// <remarks>
        /// Contrary to the osu-web implementation, this does not check for restriction status too.
        /// Use <see cref="IsUserRestrictedAsync"/> separately instead.
        /// </remarks>
        /// <seealso href="https://github.com/ppy/osu-web/blob/65ca10d9b137009c5a33876b4caef3453dfb0bc2/app/Models/User.php#L1114-L1127"><c>isSilenced()</c> in osu-web</seealso>
        public static async Task<bool> IsUserSilencedAsync(this MySqlConnection db, uint userId, MySqlTransaction? transaction = null)
        {
            var ban = await db.QueryFirstOrDefaultAsync<osu_user_banhistory>(
                @"SELECT * FROM `osu_user_banhistory` WHERE `user_id` = @user_id AND `ban_status` IN (1, 2) ORDER BY `timestamp` DESC",
                new
                {
                    user_id = userId,
                },
                transaction);

            return ban != null && ban.period != 0 && ban.EndTime > DateTimeOffset.Now;
        }

        public static async Task<ulong> GetUserMonthlyPlaycountAsync(this MySqlConnection db, uint userId, MySqlTransaction? transaction = null)
        {
            return await db.QuerySingleAsync<ulong?>(
                @"SELECT SUM(`playcount`) FROM `osu_user_month_playcount` WHERE `user_id` = @user_id",
                new
                {
                    user_id = userId,
                },
                transaction) ?? 0;
        }

        public static async Task PurgeInactiveBeatmapSetsForUserAsync(this MySqlConnection db, uint userId, MySqlTransaction? transaction = null)
        {
            uint[] beatmapSetIds = (await db.QueryAsync<uint>(@"SELECT `beatmapset_id` FROM `osu_beatmapsets` WHERE `user_id` = @user_id AND `active` = -1 AND `deleted_at` IS NULL",
                new
                {
                    user_id = userId,
                },
                transaction)).ToArray();

            await db.ExecuteAsync(@"DELETE FROM `osu_beatmaps` WHERE `beatmapset_id` IN @beatmapset_ids AND `user_id` = @user_id AND `deleted_at` IS NULL",
                new
                {
                    beatmapset_ids = beatmapSetIds,
                    user_id = userId,
                }, transaction);
            await db.ExecuteAsync(@"DELETE FROM `osu_beatmapsets` WHERE `user_id` = @user_id AND `active` = -1",
                new
                {
                    user_id = userId,
                },
                transaction);
        }

        public static Task<(uint unranked, uint ranked)> GetUserBeatmapSetCountAsync(this MySqlConnection db, uint userId, MySqlTransaction? transaction = null)
        {
            return db.QuerySingleAsync<(uint unranked, uint ranked)>(
                """
                SELECT
                    SUM(CASE WHEN `approved` IN (-1, 0) THEN 1 ELSE 0 END) AS `unranked`,
                    SUM(CASE WHEN `approved` > 0 THEN 1 ELSE 0 END) AS `ranked`
                FROM `osu_beatmapsets` WHERE `active` > 0 AND `deleted_at` IS NULL AND `user_id` = @user_id
                """,
                new
                {
                    user_id = userId,
                },
                transaction);
        }

        public static Task<bool> IsUserSupporterAsync(this MySqlConnection db, uint userId, MySqlTransaction? transaction = null)
        {
            return db.QuerySingleAsync<bool>(@"SELECT `osu_subscriber` FROM `phpbb_users` WHERE `user_id` = @user_id",
                new
                {
                    user_id = userId,
                },
                transaction);
        }

        public static Task<osu_beatmapset?> GetBeatmapSetAsync(this MySqlConnection db, uint beatmapSetId, MySqlTransaction? transaction = null)
        {
            return db.QuerySingleOrDefaultAsync<osu_beatmapset?>(@"SELECT * FROM `osu_beatmapsets` WHERE `beatmapset_id` = @beatmapSetId AND `deleted_at` IS NULL",
                new
                {
                    beatmapSetId = beatmapSetId,
                },
                transaction);
        }

        public static Task<IEnumerable<uint>> GetBeatmapIdsInSetAsync(this MySqlConnection db, uint beatmapSetId, MySqlTransaction? transaction = null)
        {
            return db.QueryAsync<uint>(@"SELECT `beatmap_id` FROM `osu_beatmaps` WHERE `beatmapset_id` = @beatmapSetId AND `deleted_at` IS NULL",
                new
                {
                    beatmapSetId = beatmapSetId,
                },
                transaction);
        }

        public static Task<osu_beatmap?> GetBeatmapAsync(this MySqlConnection db, uint beatmapSetId, uint beatmapId, MySqlTransaction? transaction = null)
        {
            return db.QuerySingleOrDefaultAsync<osu_beatmap?>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` = @beatmapId AND `beatmapset_id` = @beatmapSetId AND `deleted_at` IS NULL",
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

        public static async Task SetBeatmapSetOnlineStatusAsync(this MySqlConnection db, uint beatmapSetId, BeatmapOnlineStatus onlineStatus, MySqlTransaction? transaction = null)
        {
            await db.ExecuteAsync(
                """
                UPDATE `osu_beatmapsets` SET `approved` = @status WHERE `beatmapset_id` = @beatmapset_id;
                UPDATE `osu_beatmaps` SET `approved` = @status WHERE `beatmapset_id` = @beatmapset_id;
                """,
                new
                {
                    status = onlineStatus,
                    beatmapset_id = beatmapSetId,
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

        public static async Task<ulong> InsertBeatmapsetFileAsync(this MySqlConnection db, beatmapset_file file, MySqlTransaction? transaction = null)
        {
            var existing = await db.QuerySingleOrDefaultAsync<beatmapset_file?>(
                "SELECT * FROM `beatmapset_files` WHERE `sha2_hash` = @sha2_hash",
                file,
                transaction);

            if (existing != null)
            {
                if (existing.file_size != file.file_size)
                    throw new InvalidOperationException("There is already a file in the database with the same SHA2 but different size. This is very very VERY bad news.");

                return file.file_id = existing.file_id;
            }

            return file.file_id = await db.QuerySingleAsync<ulong>(
                "INSERT INTO `beatmapset_files` (`sha2_hash`, `file_size`) VALUES (@sha2_hash, @file_size) AS `new`; SELECT LAST_INSERT_ID();",
                file,
                transaction);
        }

        public static async Task<(beatmapset_version, PackageFile[])?> GetBeatmapsetVersionAsync(this MySqlConnection db, uint beatmapSetId, ulong versionId, MySqlTransaction? transaction = null)
        {
            var version = await db.QuerySingleOrDefaultAsync<beatmapset_version?>(
                "SELECT * FROM `beatmapset_versions` WHERE `beatmapset_id` = @beatmapset_id AND `version_id` = @version_id",
                new
                {
                    beatmapset_id = beatmapSetId,
                    version_id = versionId
                },
                transaction);

            if (version == null)
                return null;

            PackageFile[] files = (await db.QueryAsync(
                """
                SELECT `f`.`file_id`, `f`.`sha2_hash`, `f`.`file_size`, `vf`.`file_id` AS `versioned_file_id`, `vf`.`version_id`, `vf`.`filename` FROM `beatmapset_files` `f`
                JOIN `beatmapset_version_files` `vf` ON `f`.`file_id` = `vf`.`file_id`
                WHERE `vf`.`version_id` = @version_id
                """,
                (beatmapset_file file, beatmapset_version_file versionFile) => new PackageFile(file, versionFile),
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
                "SELECT MAX(`version_id`) FROM `beatmapset_versions` WHERE `beatmapset_id` = @beatmapset_id",
                new
                {
                    beatmapset_id = beatmapSetId
                },
                transaction);

            return await db.QuerySingleAsync<ulong>(
                "INSERT INTO `beatmapset_versions` (`beatmapset_id`, `previous_version_id`) VALUES (@beatmapset_id, @previous_version_id); SELECT LAST_INSERT_ID();",
                new
                {
                    beatmapset_id = beatmapSetId,
                    previous_version_id = previousVersion,
                },
                transaction);
        }

        public static Task InsertBeatmapsetVersionFileAsync(this MySqlConnection db, beatmapset_version_file versionFile, MySqlTransaction? transaction = null)
        {
            return db.ExecuteAsync(
                "INSERT INTO `beatmapset_version_files` (`file_id`, `version_id`, `filename`) VALUES (@file_id, @version_id, @filename)",
                versionFile,
                transaction);
        }

        public static async Task<bool> IsBeatmapSetInProcessingQueueAsync(this MySqlConnection db, uint beatmapSetId, MySqlTransaction? transaction = null)
        {
            return await db.QuerySingleAsync<uint>(
                "SELECT COUNT(1) FROM `bss_process_queue` WHERE `beatmapset_id` = @beatmapset_id AND `status` = 0",
                new
                {
                    beatmapset_id = beatmapSetId
                },
                transaction) > 0;
        }

        public static Task AddBeatmapSetToProcessingQueueAsync(this MySqlConnection db, uint beatmapSetId, MySqlTransaction? transaction = null)
        {
            return db.ExecuteAsync(
                "INSERT INTO `bss_process_queue` (`beatmapset_id`) VALUES (@beatmapset_id)",
                new
                {
                    beatmapset_id = beatmapSetId
                },
                transaction);
        }

        public static async Task<bool> IsBeatmapSetNominatedAsync(this MySqlConnection db, uint beatmapSetId, MySqlTransaction? transaction = null)
        {
            return await db.QuerySingleOrDefaultAsync<string>(
                "SELECT `type` FROM `beatmapset_events` WHERE `beatmapset_id` = @beatmapset_id AND `type` IN ('nominate', 'nomination_reset', 'disqualify') ORDER BY `created_at` DESC LIMIT 1",
                new
                {
                    beatmapset_id = beatmapSetId
                },
                transaction) == "nominate";
        }
    }
}
