// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Net;

namespace osu.Server.BeatmapSubmission.Tests
{
    public class HelloOsuControllerTest : IntegrationTest
    {
        public HelloOsuControllerTest(TestWebApplicationFactory<Program> webAppFactory)
            : base(webAppFactory)
        {
        }

        [Fact]
        public async Task HelloWorks()
        {
            var response = await Client.GetAsync("hello");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("0", await response.Content.ReadAsStringAsync());
        }
    }
}
