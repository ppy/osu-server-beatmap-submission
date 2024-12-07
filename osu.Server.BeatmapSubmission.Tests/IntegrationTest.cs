// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using Dapper;
using osu.Server.QueueProcessor;

namespace osu.Server.BeatmapSubmission.Tests
{
    [Collection("Integration Tests")] // ensures sequential execution
    public abstract class IntegrationTest : IClassFixture<IntegrationTestWebApplicationFactory<Program>>, IDisposable
    {
        protected readonly HttpClient Client;

        protected CancellationToken CancellationToken => cancellationSource.Token;
        private readonly CancellationTokenSource cancellationSource;

        protected IntegrationTest(IntegrationTestWebApplicationFactory<Program> webAppFactory)
        {
            Client = webAppFactory.CreateClient();
            reinitialiseDatabase();

            cancellationSource = Debugger.IsAttached
                ? new CancellationTokenSource()
                : new CancellationTokenSource(20000);
        }

        private void reinitialiseDatabase()
        {
            using var db = DatabaseAccess.GetConnection();

            // just a safety measure for now to ensure we don't hit production.
            // will throw if not on test database.
            if (db.QueryFirstOrDefault<int?>("SELECT `count` FROM `osu_counts` WHERE name = 'is_production'") != null)
                throw new InvalidOperationException("You have just attempted to run tests on production and wipe data. Rethink your life decisions.");

            db.Execute("TRUNCATE TABLE `osu_user_banhistory`");
            db.Execute("TRUNCATE TABLE `osu_user_month_playcount`");
            db.Execute("TRUNCATE TABLE `phpbb_users`");
            db.Execute("TRUNCATE TABLE `osu_beatmaps`");

            db.Execute("TRUNCATE TABLE `beatmapset_version_files`");
            db.Execute("TRUNCATE TABLE `beatmapset_files`");
            db.Execute("TRUNCATE TABLE `beatmapset_versions`");
            db.Execute("TRUNCATE TABLE `beatmapset_events`");
            db.Execute("TRUNCATE TABLE `osu_beatmapsets`");
            db.Execute("TRUNCATE TABLE `beatmap_owners`");
        }

        protected void WaitForDatabaseState<T>(string sql, T expected, CancellationToken cancellationToken, object? param = null)
        {
            using (var db = DatabaseAccess.GetConnection())
            {
                T? lastValue = default;

                while (true)
                {
                    if (!Debugger.IsAttached)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw new TimeoutException($"Waiting for database state took too long (expected: {expected} last: {lastValue} sql: {sql})");
                    }

                    lastValue = db.QueryFirstOrDefault<T>(sql, param);

                    if ((expected == null && lastValue == null) || expected?.Equals(lastValue) == true)
                        return;

                    Thread.Sleep(50);
                }
            }
        }

        public virtual void Dispose()
        {
            Client.Dispose();
            cancellationSource.Dispose();
        }
    }
}
