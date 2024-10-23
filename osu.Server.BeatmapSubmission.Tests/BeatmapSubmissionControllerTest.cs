// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Reflection;
using osu.Server.BeatmapSubmission.Tests.Resources;

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
            var request = new HttpRequestMessage(HttpMethod.Put, "/beatmapsets/241526");

            const string filename = "241526 Soleily - Renatus.osz";
            using var content = new MultipartFormDataContent($"{Guid.NewGuid()}----");
            using var stream = TestResources.GetResource(filename)!;
            content.Add(new StreamContent(stream), "beatmapArchive", filename);
            request.Content = content;

            var response = await Client.SendAsync(request);
            Assert.Equal("6095156", await response.Content.ReadAsStringAsync());
        }
    }
}
