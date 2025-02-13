// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using osu.Framework.Extensions;
using osu.Framework.Utils;
using osu.Game.Beatmaps;
using osu.Server.BeatmapSubmission.Authentication;
using osu.Server.BeatmapSubmission.Models.API.Requests;
using osu.Server.BeatmapSubmission.Models.API.Responses;
using osu.Server.BeatmapSubmission.Models.Database;
using osu.Server.BeatmapSubmission.Services;
using osu.Server.BeatmapSubmission.Tests.Resources;
using osu.Server.QueueProcessor;
using SharpCompress.Archives.Zip;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace osu.Server.BeatmapSubmission.Tests
{
    public class BeatmapSubmissionControllerTest : IntegrationTest
    {
        private const string osz_filename = "241526 Soleily - Renatus.osz";
        private const string osu_filename = "Soleily - Renatus (test) [Platter 2].osu";

        protected new HttpClient Client { get; }

        private readonly LocalBeatmapStorage beatmapStorage;
        private readonly Mock<ILegacyIO> mockLegacyIO = new Mock<ILegacyIO>();

        public BeatmapSubmissionControllerTest(IntegrationTestWebApplicationFactory<Program> webAppFactory)
            : base(webAppFactory)
        {
            beatmapStorage = new LocalBeatmapStorage(Directory.CreateTempSubdirectory(nameof(BeatmapSubmissionControllerTest)).FullName);

            Client = webAppFactory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddTransient<IBeatmapStorage>(_ => beatmapStorage);
                    services.AddTransient<BeatmapPackagePatcher>();
                    services.AddTransient<ILegacyIO>(_ => mockLegacyIO.Object);
                    services.AddTransient<IMirrorService, NoOpMirrorService>();
                });
            }).CreateClient();
        }

        [Theory]
        [InlineData(BeatmapSubmissionTarget.WIP)]
        [InlineData(BeatmapSubmissionTarget.Pending)]
        public async Task TestPutBeatmapSet_NewSet(BeatmapSubmissionTarget status)
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2, '2411', 5)");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapsToCreate = 15,
                Target = status,
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmapsets`", 1, CancellationToken);
            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps`", 15, CancellationToken);

            var beatmapset = await db.QuerySingleAsync<osu_beatmapset>("SELECT * FROM `osu_beatmapsets`");
            Assert.Equal(2u, beatmapset.user_id);
            Assert.Equal("test", beatmapset.creator);
            Assert.Equal((int)status, (int)beatmapset.approved);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = @beatmapSetId", 15, CancellationToken, new { beatmapSetId = beatmapset.beatmapset_id });
            WaitForDatabaseState(@"SELECT `versions_available` FROM `osu_beatmapsets` WHERE `beatmapset_id` = @beatmapSetId", 15, CancellationToken, new { beatmapSetId = beatmapset.beatmapset_id });

            var responseContent = await response.Content.ReadFromJsonAsync<PutBeatmapSetResponse>();
            Assert.Equal(15, responseContent!.BeatmapIds.Count);
        }

        [Fact]
        public async Task TestPutBeatmapSet_RestrictedUserCannotSubmit()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`, `user_warnings`) VALUES (2, 'test', 'test', 'JP', '', '', '', '', 1)");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest { BeatmapsToCreate = 15 });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("Your account is currently restricted.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_SilencedUserCannotSubmit()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync(
                """
                   INSERT INTO `osu_user_banhistory` (`user_id`, `ban_status`, `period`, `timestamp`)
                   VALUES
                        (2, 2, 86400, CURRENT_TIMESTAMP()),
                        (2, 2, 86400, TIMESTAMPADD(YEAR, -1, CURRENT_TIMESTAMP()))
                   """);

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest { BeatmapsToCreate = 15 });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("You are unable to submit or update maps while silenced.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_UserWithTooLowPlaycountCannotSubmit()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2, '2411', 3)");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest { BeatmapsToCreate = 15 });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("Thanks for your contribution, but please play the game first!", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_SilencedUserCanSubmitAfterSilenceExpires()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2, '2411', 5)");
            await db.ExecuteAsync("INSERT INTO `osu_user_banhistory` (`user_id`, `ban_status`, `period`, `timestamp`) VALUES (2, 2, 86400, TIMESTAMPADD(DAY, -1, CURRENT_TIMESTAMP()))");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest { BeatmapsToCreate = 15 });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);
            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmapsets`", 1, CancellationToken);
            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps`", 15, CancellationToken);
        }

        [Fact]
        public async Task TestPutBeatmapSet_NewSet_CannotSpecifyBeatmapsToKeep()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2, '2411', 5)");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapsToCreate = 15,
                BeatmapsToKeep = [1, 2, 3],
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("Cannot specify beatmaps to keep when creating a new beatmap set.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_NewSet_CannotExceedMaxBeatmapLimit()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2, '2411', 5)");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapsToCreate = 129,
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("The beatmap set cannot contain more than 128 beatmaps.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_NewSet_BeatmapSetQuotaExceeded()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2, '2411', 5)");

            for (int i = 0; i < 4; ++i)
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (2, 'test user', -1, 0, 1, CURRENT_TIMESTAMP)");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest { BeatmapsToCreate = 15 });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("You have exceeded your submission cap", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_NewSet_InactiveBeatmapsArePurged()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2, '2411', 5)");

            for (int i = 0; i < 4; ++i)
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (2, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest { BeatmapsToCreate = 15 });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmapsets`", 1, CancellationToken);
            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps`", 15, CancellationToken);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 1000, 'test user', -1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, 1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [5001, 5003, 5005],
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = 1000 AND `deleted_at` IS NULL", 6, CancellationToken);
            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = 1000 AND `deleted_at` IS NOT NULL", 2, CancellationToken);
            WaitForDatabaseState(@"SELECT `deleted_at` FROM `osu_beatmaps` WHERE `beatmap_id` = @beatmapId", (DateTimeOffset?)null, CancellationToken, new { beatmapId = 5001 });
            WaitForDatabaseState(@"SELECT `deleted_at` FROM `osu_beatmaps` WHERE `beatmap_id` = @beatmapId", (DateTimeOffset?)null, CancellationToken, new { beatmapId = 5003 });
            WaitForDatabaseState(@"SELECT `deleted_at` FROM `osu_beatmaps` WHERE `beatmap_id` = @beatmapId", (DateTimeOffset?)null, CancellationToken, new { beatmapId = 5005 });
            WaitForDatabaseState(@"SELECT `versions_available` FROM `osu_beatmapsets` WHERE `beatmapset_id` = 1000", 6, CancellationToken);

            var responseContent = await response.Content.ReadFromJsonAsync<PutBeatmapSetResponse>();
            Assert.Equal(6, responseContent!.BeatmapIds.Count);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet_MoveToPending()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync(
                "INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            await db.ExecuteAsync(
                @"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 1000, 'test user', -1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [5001, 5003, 5005],
                Target = BeatmapSubmissionTarget.Pending,
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = 1000 AND `deleted_at` IS NULL AND `approved` = -1", 0, CancellationToken);
            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = 1000 AND `deleted_at` IS NULL AND `approved` = 0", 6, CancellationToken);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet_StatusShenanigansNotAllowed()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync(
                "INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            await db.ExecuteAsync(
                @"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 1000, 'test user', -1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [5001, 5003, 5005],
                Target = (BeatmapSubmissionTarget)1, // cheeky attempt to "rank" a set using parameter abuse
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = 1000 AND `deleted_at` IS NULL AND `approved` = -1", 5, CancellationToken);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet_CannotModifyDeletedSet()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`, `deleted_at`) VALUES (1000, 1000, 'test user', -1, 0, 1, CURRENT_TIMESTAMP, NOW())");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `deleted_at`) VALUES (@beatmapId, 1000, 1000, 1, NOW())", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [5001, 5003, 5005],
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet_CannotModifyIfNotOwner()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2000, '2411', 5)");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [5001, 5003, 5005],
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet_CannotModifyIfRanked()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2000, '2411', 5)");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 1000, 'test user', 1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, 1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [5001, 5003, 5005],
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet_CannotKeepBeatmapsThatWereNotPartOfTheSet()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 1000, 'test user', -1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, 1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [9999],
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("One of the beatmaps to keep does not belong to the specified set.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet_CannotDeleteAllBeatmaps()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 1000, 'test user', -1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, 1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("The beatmap set must contain at least one beatmap.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet_CannotExceedMaxBeatmapLimit()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 1000, 'test user', -1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, 1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToKeep = [5001, 5002, 5003, 5004, 5005],
                BeatmapsToCreate = 124,
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("The beatmap set cannot contain more than 128 beatmaps.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_ExistingSet_BeatmapSetQuotaExceeded()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`, `osu_subscriber`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '', 1)");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            for (int i = 0; i < 9; ++i)
            {
                await db.ExecuteAsync(
                    @"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (@id, 1000, 'test user', @status, 0, 1, CURRENT_TIMESTAMP)",
                    new
                    {
                        id = i + 1000,
                        status = i == 0 ? BeatmapOnlineStatus.Graveyard : BeatmapOnlineStatus.WIP
                    });
            }

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, 1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [5001, 5003, 5005],
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("Beatmap is in the graveyard and you don't have enough remaining upload quota to resurrect it.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPutBeatmapSet_NonexistentSet()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Theory]
        [InlineData(BeatmapSubmissionTarget.WIP)]
        [InlineData(BeatmapSubmissionTarget.Pending)]
        public async Task TestPutBeatmapSet_ExistingSet_BeatmapRevivedFromGraveyard(BeatmapSubmissionTarget status)
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`, `osu_subscriber`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '', 1)");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            await db.ExecuteAsync("INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 1000, 'test user', -2, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync("INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, -2)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [5001, 5003, 5005],
                Target = status,
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);
            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_beatmapsets` WHERE `approved` = @approved", 1, CancellationToken, new { approved = (int)status });
            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_beatmapsets` WHERE `approved` = -2", 0, CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_beatmaps` WHERE `approved` = @approved AND `deleted_at` IS NULL", 6, CancellationToken, new { approved = (int)status });
            mockLegacyIO.Verify(m => m.BroadcastReviveBeatmapSetEventAsync(1000), Times.Once);
        }

        [Theory]
        [InlineData(BeatmapSubmissionTarget.WIP)]
        [InlineData(BeatmapSubmissionTarget.Pending)]
        public async Task TestPutBeatmapSet_ExistingSet_RevivalFailsDueToDownloadDisabled(BeatmapSubmissionTarget status)
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`, `osu_subscriber`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '', 1)");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (1000, '2411', 5)");

            await db.ExecuteAsync("INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`, `download_disabled`) VALUES (1000, 1000, 'test user', -2, 0, 1, CURRENT_TIMESTAMP, 1)");

            foreach (uint beatmapId in new uint[] { 5001, 5002, 5003, 5004, 5005 })
                await db.ExecuteAsync("INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 1000, -2)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            request.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = 1000,
                BeatmapsToCreate = 3,
                BeatmapsToKeep = [5001, 5003, 5005],
                Target = status,
            });
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("Could not perform this action. Please send an e-mail to support@ppy.sh to follow up on this.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestUploadFullPackage()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource(osz_filename)!;
            content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT `title` FROM `osu_beatmapsets` WHERE `beatmapset_id` = 241526", "Renatus", CancellationToken);

            var beatmapset = await db.QuerySingleAsync<osu_beatmapset>(@"SELECT * FROM `osu_beatmapsets` WHERE `beatmapset_id` = 241526");

            Assert.Equal("Soleily", beatmapset.artist);
            Assert.Equal("Soleily", beatmapset.artist_unicode);
            Assert.Equal("Renatus", beatmapset.title);
            Assert.Equal("Renatus", beatmapset.title_unicode);
            Assert.Equal(string.Empty, beatmapset.source);
            Assert.Equal("MBC7 Unisphere 地球ヤバイEP Chikyu Yabai", beatmapset.tags);
            Assert.False(beatmapset.video);
            Assert.False(beatmapset.storyboard);
            Assert.True(Precision.AlmostEquals(beatmapset.bpm, 182));
            Assert.Equal("241526.osz", beatmapset.filename);
            Assert.True(beatmapset.active);

            var osuBeatmap = await db.QuerySingleAsync<osu_beatmap>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` = 557814");

            Assert.Equal("Soleily - Renatus (test) [Hard].osu", osuBeatmap.filename);
            Assert.Equal("30ae41c14d211ea5cdc128e347573a9a", osuBeatmap.checksum);
            Assert.Equal("Hard", osuBeatmap.version);
            Assert.Equal(208u, osuBeatmap.total_length); // off by 1 compared to osu-web, but doesn't seem worth investigating
            Assert.Equal(190u, osuBeatmap.hit_length); // off by 1 compared to osu-web, but doesn't seem worth investigating
            Assert.Equal(558u, osuBeatmap.countTotal);
            Assert.Equal(160u, osuBeatmap.countNormal);
            Assert.Equal(396u, osuBeatmap.countSlider);
            Assert.Equal(2u, osuBeatmap.countSpinner);
            Assert.Equal(4f, osuBeatmap.diff_size);
            Assert.Equal(6f, osuBeatmap.diff_drain);
            Assert.Equal(6f, osuBeatmap.diff_overall);
            Assert.Equal(7.5f, osuBeatmap.diff_approach);
            Assert.Equal(0, osuBeatmap.playmode);

            var maniaBeatmap = await db.QuerySingleAsync<osu_beatmap>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` = 557813");

            Assert.Equal("Soleily - Renatus (test) [Hyper].osu", maniaBeatmap.filename);
            Assert.Equal("3c4ac5fe3ed0abe7ee9eb000d4d06ebc", maniaBeatmap.checksum);
            Assert.Equal("Hyper", maniaBeatmap.version);
            Assert.Equal(226u, maniaBeatmap.total_length); // off by 2 compared to osu-web, but doesn't seem worth investigating
            Assert.Equal(226u, maniaBeatmap.hit_length); // off by 1 compared to osu-web, but doesn't seem worth investigating
            Assert.Equal(1706u, maniaBeatmap.countTotal);
            Assert.Equal(1657u, maniaBeatmap.countNormal);
            Assert.Equal(49u, maniaBeatmap.countSlider);
            Assert.Equal(0u, maniaBeatmap.countSpinner);
            Assert.Equal(7f, maniaBeatmap.diff_size);
            Assert.Equal(7.5f, maniaBeatmap.diff_drain);
            Assert.Equal(7.5f, maniaBeatmap.diff_overall);
            Assert.Equal(5f, maniaBeatmap.diff_approach);
            Assert.Equal(3, maniaBeatmap.playmode);

            mockLegacyIO.Verify(lio => lio.BroadcastNewBeatmapSetEventAsync(241526), Times.Once);
        }

        [Fact]
        public async Task TestPutBeatmapSet_DoNotPurgeSetImmediatelyAfterCreatingIt()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `osu_user_month_playcount` (`user_id`, `year_month`, `playcount`) VALUES (2, '2411', 5)");

            var createRequest = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            createRequest.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapsToCreate = 1,
                Target = BeatmapSubmissionTarget.Pending,
            });
            createRequest.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var createResponse = await Client.SendAsync(createRequest);
            Assert.True(createResponse.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmapsets`", 1, CancellationToken);
            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps`", 1, CancellationToken);

            var createResponseContent = await createResponse.Content.ReadFromJsonAsync<PutBeatmapSetResponse>();
            Assert.NotNull(createResponseContent);

            var updateRequest = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets");
            updateRequest.Content = JsonContent.Create(new PutBeatmapSetRequest
            {
                BeatmapSetID = createResponseContent.BeatmapSetId,
                BeatmapsToKeep = createResponseContent.BeatmapIds.ToArray(),
                Target = BeatmapSubmissionTarget.Pending,
            });
            updateRequest.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2");

            var updateResponse = await Client.SendAsync(updateRequest);
            Assert.True(updateResponse.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmapsets`", 1, CancellationToken);
            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps`", 1, CancellationToken);
        }

        [Fact]
        public async Task TestUploadFullPackage_DoesNothingIfPreviousVersionIsTheSame()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            // first upload
            {
                var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

                using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
                using var stream = TestResources.GetResource(osz_filename)!;
                content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
                request.Content = content;
                request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

                var response = await Client.SendAsync(request);
                Assert.True(response.IsSuccessStatusCode);
                mockLegacyIO.Verify(lio => lio.BroadcastNewBeatmapSetEventAsync(241526), Times.Once);
            }

            mockLegacyIO.Invocations.Clear();

            // second upload, using same file
            {
                var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

                using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
                using var stream = TestResources.GetResource(osz_filename)!;
                content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
                request.Content = content;
                request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

                var response = await Client.SendAsync(request);
                Assert.True(response.IsSuccessStatusCode);
                mockLegacyIO.VerifyNoOtherCalls();
            }
        }

        [Fact]
        public async Task TestUploadFullPackage_DeletedBeatmapsDoNotInterfere()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `deleted_at`) VALUES (999999, 1000, 241526, -1, NOW())");

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource(osz_filename)!;
            content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT `title` FROM `osu_beatmapsets` WHERE `beatmapset_id` = 241526", "Renatus", CancellationToken);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsIfBeatmapDeleted()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`, `deleted_at`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP, NOW())");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `deleted_at`) VALUES (@beatmapId, 1000, 241526, -1, NOW())", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource(osz_filename)!;
            content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsIfBeatmapRanked()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', 1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, 1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");

            using var memoryStream = new MemoryStream();

            using (new ZipWriter(memoryStream, BeatmapPackagePatcher.DEFAULT_ZIP_WRITER_OPTIONS))
            {
            }

            content.Add(new StreamContent(memoryStream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsOnGraveyardedBeatmaps()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            for (int i = 0; i < 4; ++i)
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 'test user', -1, 0, 1, CURRENT_TIMESTAMP)");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -2, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -2)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource(osz_filename)!;
            content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("The beatmap set must be revived first.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsOnEmptyPackage()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");

            using var memoryStream = new MemoryStream();

            using (new ZipWriter(memoryStream, BeatmapPackagePatcher.DEFAULT_ZIP_WRITER_OPTIONS))
            {
            }

            content.Add(new StreamContent(memoryStream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal("The uploaded beatmap set must have at least one difficulty.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsOnPackageWithSuspiciousFile()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");

            using var memoryStream = new MemoryStream();

            using (var zipWriter = new ZipWriter(memoryStream, BeatmapPackagePatcher.DEFAULT_ZIP_WRITER_OPTIONS))
                zipWriter.Write("bad.dll", new MemoryStream("i am a bad file >:)"u8.ToArray()));

            content.Add(new StreamContent(memoryStream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains("Beatmap contains an unsupported file type", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsIfBeatmapsDoNotHaveCorrectSetIDsInside()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (999999, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 999999, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/999999");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource(osz_filename)!;
            content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Beatmap has invalid beatmap set ID inside", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsIfBeatmapsDoNotHaveCorrectIDsInside()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 1 }) // last ID will not match
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource(osz_filename)!;
            content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Beatmap has invalid ID inside", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Theory]
        [InlineData("../suspicious")]
        [InlineData("..\\suspicious")]
        [InlineData("a/../../../suspicious")]
        [InlineData("b\\..\\..\\..\\suspicious")]
        public async Task TestUploadFullPackage_PathTraversalAttackFails(string suspiciousFilename)
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            var dstStream = new MemoryStream();
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            dstStream.Seek(0, SeekOrigin.Begin);

            using (var archive = ZipArchive.Open(dstStream))
            {
                archive.AddEntry(suspiciousFilename, new MemoryStream("i am doing dodgy stuff"u8.ToArray()));
                dstStream = new MemoryStream();
                archive.SaveTo(dstStream, BeatmapPackagePatcher.DEFAULT_ZIP_WRITER_OPTIONS);
            }

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            content.Add(new StreamContent(dstStream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsIfSizeTooLarge()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            byte[] data = new byte[50 * 1024 * 1024];
            new Random(1337).NextBytes(data);
            var stream = new MemoryStream();

            using (var zipWriter = new ZipWriter(stream, BeatmapPackagePatcher.DEFAULT_ZIP_WRITER_OPTIONS))
            {
                zipWriter.Write("garbage.png", new MemoryStream(data));
                using var osuFileStream = TestResources.GetResource(osu_filename)!;
                zipWriter.Write("test.osu", osuFileStream);
            }

            content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains("The beatmap package is too large.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsIfCreatorDoesNotMatchHostUsername()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'not test', 'not test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource(osz_filename)!;
            content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains("At least one difficulty has a specified creator that isn't the beatmap host's username.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestUploadFullPackage_FailsIfNonUnicodeMetadataHasMetadataChars()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557810 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource("non-ascii-chars-in-ascii-metadata.osz")!;
            content.Add(new StreamContent(stream), "beatmapArchive", osz_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Romanised title contains disallowed characters.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPatchPackage()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", osu_filename);
            content.Add(new StringContent("Soleily - Renatus (test) [Platter].osu"), "filesDeleted");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = 241526 AND `deleted_at` IS NULL", 12, CancellationToken);

            var renamedBeatmap = await db.QuerySingleAsync<osu_beatmap>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` = 557810");
            Assert.Equal("Platter 2", renamedBeatmap.version);
            mockLegacyIO.Verify(io => io.DisqualifyBeatmapSetAsync(241526, It.IsAny<string>()), Times.Never);
            mockLegacyIO.Verify(lio => lio.BroadcastUpdateBeatmapSetEventAsync(241526, 1000), Times.Once);
        }

        [Fact]
        public async Task TestPatchPackage_AddStoryboard()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            const string storyboard_content =
                """
                osu file format v14

                [Events]
                Animation,Foreground,Centre,"forever-string.png",330,240,10,108,LoopForever
                """;

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StringContent(storyboard_content), "filesChanged", "storyboard.osb");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmapsets` WHERE `beatmapset_id` = 241526 AND `deleted_at` IS NULL AND `storyboard` = 1", 1, CancellationToken);
        }

        [Fact]
        public async Task TestPatchPackage_NominationsReset()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync("INSERT INTO `beatmapset_events` (`beatmapset_id`, `user_id`, `type`) VALUES (241526, 1001, 'nominate')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", osu_filename);
            content.Add(new StringContent("Soleily - Renatus (test) [Platter].osu"), "filesDeleted");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = 241526 AND `deleted_at` IS NULL", 12, CancellationToken);
            mockLegacyIO.Verify(io => io.DisqualifyBeatmapSetAsync(241526, It.IsAny<string>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task TestPatchPackage_FailsIfThereWasNoPriorUpload()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task TestPatchPackage_FailsIfBeatmapDeleted()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`, `deleted_at`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP, NOW())");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `deleted_at`) VALUES (@beatmapId, 1000, 241526, -1, NOW())", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", osu_filename);
            content.Add(new StringContent("Soleily - Renatus (test) [Platter].osu"), "filesDeleted");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task TestPatchPackage_FailsIfBeatmapRanked()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', 1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, 1)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", osu_filename);
            content.Add(new StringContent("Soleily - Renatus (test) [Platter].osu"), "filesDeleted");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task TestPatchPackage_FailsOnGraveyardedBeatmaps()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            for (int i = 0; i < 4; ++i)
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (1000, 'test user', -1, 0, 1, CURRENT_TIMESTAMP)");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -2, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -2)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", osu_filename);
            content.Add(new StringContent("Soleily - Renatus (test) [Platter].osu"), "filesDeleted");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("The beatmap set must be revived first.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPatchPackage_FailsOnSuspiciousFileTypes()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", "suspicious.PY");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Beatmap contains an unsupported file type", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPatchPackage_FailsIfBeatmapDoesNotHaveCorrectSetIDInside()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (999999, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 999999, -1)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "999999")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (999999)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/999999");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", osu_filename);
            content.Add(new StringContent("Soleily - Renatus (test) [Platter].osu"), "filesDeleted");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Beatmap has invalid beatmap set ID inside", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPatchPackage_FailsIfBeatmapDoesNotHaveCorrectIDInside()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 1, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", osu_filename);
            content.Add(new StringContent("Soleily - Renatus (test) [Platter].osu"), "filesDeleted");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Beatmap has invalid ID inside", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Theory]
        [InlineData("../suspicious")]
        [InlineData("..\\suspicious")]
        [InlineData("a/../../../suspicious")]
        [InlineData("b\\..\\..\\..\\suspicious")]
        public async Task TestPatchPackage_PathTraversalFails(string suspiciousPath)
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            content.Add(new StringContent("hello"), "filesChanged", suspiciousPath);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
        }

        [Fact]
        public async Task TestPatchPackage_FailsIfSizeTooLarge()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            byte[] data = new byte[50 * 1024 * 1024];
            new Random(1337).NextBytes(data);
            content.Add(new ByteArrayContent(data), "filesChanged", "some_large_file.mp4");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains("The beatmap package is too large.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestPatchPackage_FailsIfCreatorDoesNotMatchHostUsername()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'not test', 'not test', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "filesChanged", osu_filename);
            content.Add(new StringContent("Soleily - Renatus (test) [Platter].osu"), "filesDeleted");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "1000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains("At least one difficulty has a specified creator that isn't the beatmap host's username.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_OldStyle()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 2000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = 241526 AND `deleted_at` IS NULL", 12, CancellationToken);

            var renamedBeatmap = await db.QuerySingleAsync<osu_beatmap>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` = 557810");
            Assert.Equal("Platter 2", renamedBeatmap.version);

            mockLegacyIO.Verify(lio => lio.BroadcastUpdateBeatmapSetEventAsync(241526, 2000), Times.Once);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_NewStyle()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 1000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");
            await db.ExecuteAsync(@"INSERT INTO `beatmap_owners` (`user_id`, `beatmap_id`) VALUES (2000, 557810)");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);

            WaitForDatabaseState(@"SELECT COUNT(1) FROM `osu_beatmaps` WHERE `beatmapset_id` = 241526 AND `deleted_at` IS NULL", 12, CancellationToken);

            var renamedBeatmap = await db.QuerySingleAsync<osu_beatmap>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` = 557810");
            Assert.Equal("Platter 2", renamedBeatmap.version);

            mockLegacyIO.Verify(lio => lio.BroadcastUpdateBeatmapSetEventAsync(241526, 2000), Times.Once);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsIfBeatmapDeleted()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`, `deleted_at`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP, NOW())");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `deleted_at`) VALUES (@beatmapId, 1000, 241526, -1, NOW())", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `deleted_at`) VALUES (557810, 2000, 241526, -1, NOW())");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsIfBeatmapRanked()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', 1, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, 1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 2000, 241526, 1, 'Soleily - Renatus (test) [Platter].osu')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsIfBeatmapInGraveyard()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -2, 0, 1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -2)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 2000, 241526, -2, 'Soleily - Renatus (test) [Platter].osu')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("The beatmap set is in the graveyard. Please ask the set owner to revive it first.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsWhenDoneByAnotherUser_OldStyle()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (3000, 'not guest', 'not guest', 'JP', '', '', '', '')");
            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 2000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "3000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsWhenDoneByAnotherUser_NewStyle()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (4000, 'not guest', 'not guest', 'JP', '', '', '', '')");
            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 3000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 3000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 3000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");
            await db.ExecuteAsync(@"INSERT INTO `beatmap_owners` (`user_id`, `beatmap_id`) VALUES (2000, 557810)");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "4000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsOnSuspiciousFileTypes()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 2000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", "suspicious.doc");
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Beatmap contains an unsupported file type", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsWhenTryingToOverwriteACompletelyDifferentFile()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 2000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", "Soleily - Renatus (test) [Futsuu].osu"); // uses filename of an existing, completely different file
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Chosen filename conflicts with another existing file", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsIfBeatmapDoesNotHaveCorrectSetIDInside()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (999999, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 999999, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 2000, 999999, -1, 'Soleily - Renatus (test) [Platter].osu')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "999999")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (999999)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/999999/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Beatmap has invalid beatmap set ID inside", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsIfBeatmapDoesNotHaveCorrectIDInside()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 2000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            string osuFileContents = Encoding.UTF8.GetString(await osuFileStream.ReadAllBytesToArrayAsync());
            osuFileContents = osuFileContents.Replace("BeatmapID:557810", "BeatmapID:1");
            content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(osuFileContents)), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("Beatmap has invalid ID inside", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Theory]
        [InlineData("../suspicious")]
        [InlineData("..\\suspicious")]
        [InlineData("a/../../../suspicious")]
        [InlineData("b\\..\\..\\..\\suspicious")]
        public async Task TestSubmitGuestDifficulty_PathTraversalFails(string suspiciousPath)
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 2000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            content.Add(new StringContent("hello"), "beatmapContents", suspiciousPath);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsIfFileTooLarge()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 1000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");
            await db.ExecuteAsync(@"INSERT INTO `beatmap_owners` (`user_id`, `beatmap_id`) VALUES (2000, 557810)");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            byte[] osuFileContents = await osuFileStream.ReadAllBytesToArrayAsync();
            byte[] data = new byte[50 * 1024 * 1024];
            new Random(1337).NextBytes(data);
            byte[] fillerBytes = Encoding.UTF8.GetBytes("\n// " + string.Join(string.Empty, data.Select(b => b.ToString("X2"))));
            content.Add(new ByteArrayContent(osuFileContents.Concat(fillerBytes).ToArray()), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains("The beatmap package is too large.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        [Fact]
        public async Task TestSubmitGuestDifficulty_FailsIfCreatorDoesNotMatchHostUsername()
        {
            using var db = await DatabaseAccess.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (1000, 'not test', 'test', 'JP', '', '', '', '')");
            await db.ExecuteAsync("INSERT INTO `phpbb_users` (`user_id`, `username`, `username_clean`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2000, 'guest', 'guest', 'JP', '', '', '', '')");

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`, `filename`) VALUES (557810, 1000, 241526, -1, 'Soleily - Renatus (test) [Platter].osu')");
            await db.ExecuteAsync(@"INSERT INTO `beatmap_owners` (`user_id`, `beatmap_id`) VALUES (2000, 557810)");

            using (var dstStream = File.OpenWrite(Path.Combine(beatmapStorage.BaseDirectory, "241526")))
            using (var srcStream = TestResources.GetResource(osz_filename)!)
                await srcStream.CopyToAsync(dstStream);
            await db.ExecuteAsync(@"INSERT INTO `beatmapset_versions` (`beatmapset_id`) VALUES (241526)");

            var request = new HttpRequestMessage(HttpMethod.Patch, "/beatmapsets/241526/beatmaps/557810");

            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var osuFileStream = TestResources.GetResource(osu_filename)!;
            content.Add(new StreamContent(osuFileStream), "beatmapContents", osu_filename);
            request.Content = content;
            request.Headers.Add(HeaderBasedAuthenticationHandler.USER_ID_HEADER, "2000");

            var response = await Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains("At least one difficulty has a specified creator that isn't the beatmap host's username.", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        }

        public override void Dispose()
        {
            base.Dispose();
            Directory.Delete(beatmapStorage.BaseDirectory, recursive: true);
        }
    }
}
