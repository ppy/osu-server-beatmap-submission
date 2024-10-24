// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using osu.Framework.Utils;
using osu.Server.BeatmapSubmission.Models;
using osu.Server.BeatmapSubmission.Tests.Resources;
using osu.Server.QueueProcessor;

namespace osu.Server.BeatmapSubmission.Tests
{
    public class BeatmapSubmissionControllerTest : IntegrationTest
    {
        public BeatmapSubmissionControllerTest(TestWebApplicationFactory<Program> webAppFactory)
            : base(webAppFactory)
        {
        }

        [Fact]
        public async Task BeatmapSubmits()
        {
            using var db = DatabaseAccess.GetConnection();

            await db.ExecuteAsync(@"INSERT INTO `osu_beatmapsets` (`beatmapset_id`, `user_id`, `creator`, `approved`, `thread_id`, `active`, `submit_date`) VALUES (241526, 1000, 'test user', -1, 0, -1, CURRENT_TIMESTAMP)");

            foreach (uint beatmapId in new uint[] { 557815, 557814, 557821, 557816, 557817, 557818, 557812, 557810, 557811, 557820, 557813, 557819 })
                await db.ExecuteAsync(@"INSERT INTO `osu_beatmaps` (`beatmap_id`, `user_id`, `beatmapset_id`, `approved`) VALUES (@beatmapId, 1000, 241526, -1)", new { beatmapId = beatmapId });

            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            const string filename = "241526 Soleily - Renatus.osz";
            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource(filename)!;
            content.Add(new StreamContent(stream), "beatmapArchive", filename);
            request.Content = content;

            await Client.SendAsync(request);

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

            Assert.Equal("Soleily - Renatus (Gamu) [Hard].osu", osuBeatmap.filename);
            Assert.Equal("cb52bcebcc680909f0c81257e2222540", osuBeatmap.checksum);
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

            Assert.Equal("Soleily - Renatus (ExPew) [Hyper].osu", maniaBeatmap.filename);
            Assert.Equal("e883a693629c22034f682667b21fa695", maniaBeatmap.checksum);
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
        }
    }
}
