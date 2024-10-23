// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using osu.Server.QueueProcessor;

namespace osu.Server.BeatmapSubmission.Tests
{
    [Collection("Integration Tests")] // ensures sequential execution
    public abstract class IntegrationTest : IClassFixture<TestWebApplicationFactory<Program>>
    {
        protected readonly HttpClient Client;

        protected IntegrationTest(TestWebApplicationFactory<Program> webAppFactory)
        {
            Client = webAppFactory.CreateClient();
            reinitialiseDatabase();
        }

        private void reinitialiseDatabase()
        {
            using var db = DatabaseAccess.GetConnection();

            // just a safety measure for now to ensure we don't hit production.
            // will throw if not on test database.
            if (db.QueryFirstOrDefault<int?>("SELECT `count` FROM `osu_counts` WHERE name = 'is_production'") != null)
                throw new InvalidOperationException("You have just attempted to run tests on production and wipe data. Rethink your life decisions.");

            db.Execute("TRUNCATE TABLE `osu_beatmaps`");
            db.Execute("TRUNCATE TABLE `osu_beatmapsets`");
        }
    }
}
