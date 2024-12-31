// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using MySqlConnector;
using osu.Framework.Extensions;
using osu.Server.BeatmapSubmission.Models.Database;

namespace osu.Server.BeatmapSubmission.Services
{
    public class MirrorService : IMirrorService
    {
        private readonly HttpClient client;
        private readonly ILogger<MirrorService> logger;

        public MirrorService(HttpClient client, ILogger<MirrorService> logger)
        {
            this.client = client;
            this.logger = logger;
        }

        public async Task PurgeBeatmapSetAsync(MySqlConnection db, uint beatmapSetId)
        {
            osu_mirror[] mirrors = (await db.GetMirrorsRequiringUpdateAsync()).ToArray();

            foreach (var mirror in mirrors)
            {
                if (await performMirrorAction(mirror, "purge", new Dictionary<string, string> { ["s"] = beatmapSetId.ToString() }) != "1")
                    await db.MarkPendingPurgeAsync(mirror, beatmapSetId);
            }
        }

        private async Task<string?> performMirrorAction(osu_mirror mirror, string action, Dictionary<string, string> data)
        {
            logger.LogInformation("Performing action {action} (data: {data}) on mirror {mirror}", action, data, mirror.base_url);

            data["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
            data["action"] = action;
            data["cs"] = ($"{data.GetValueOrDefault("s")}{data.GetValueOrDefault("fd")}{data.GetValueOrDefault("fs")}{data.GetValueOrDefault("ts")}"
                          + $"{data.GetValueOrDefault("nv")}{data.GetValueOrDefault("action")}{mirror.secret_key}").ComputeMD5Hash();

            var request = new HttpRequestMessage(HttpMethod.Post, mirror.base_url);
            request.Content = new FormUrlEncodedContent(data);

            try
            {
                var response = await client.SendAsync(request);
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Attempting to perform action {action} on mirror {mirror} failed", action, mirror.base_url);
                return null;
            }
        }
    }

    public class NoOpMirrorService : IMirrorService
    {
        public Task PurgeBeatmapSetAsync(MySqlConnection db, uint beatmapSetId) => Task.CompletedTask;
    }
}
