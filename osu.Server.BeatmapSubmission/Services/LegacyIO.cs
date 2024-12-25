// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using osu.Server.BeatmapSubmission.Configuration;

namespace osu.Server.BeatmapSubmission.Services
{
    public class LegacyIO : ILegacyIO
    {
        private readonly HttpClient httpClient;
        private readonly ILogger<LegacyIO> logger;

        public LegacyIO(HttpClient httpClient, ILogger<LegacyIO> logger)
        {
            this.httpClient = httpClient;
            this.logger = logger;
        }

        private async Task runLegacyIO(HttpMethod method, string command, dynamic? postObject = null)
        {
            int retryCount = 3;

            retry:

            long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string url = $"{AppSettings.LegacyIODomain}/_lio/{command}{(command.Contains('?') ? "&" : "?")}timestamp={time}";

            string? serialisedPostObject = postObject == null ? null : JsonSerializer.Serialize(postObject);
            logger.LogDebug("Performing LIO request to {method} {url} (params: {params})", method, url, serialisedPostObject);

            try
            {
                string signature = hmacEncode(url, Encoding.UTF8.GetBytes(AppSettings.SharedInteropSecret));

                var httpRequestMessage = new HttpRequestMessage
                {
                    RequestUri = new Uri(url),
                    Method = method,
                    Headers =
                    {
                        { "X-LIO-Signature", signature },
                        { "Accept", "application/json" },
                    },
                };

                if (postObject != null)
                {
                    httpRequestMessage.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(serialisedPostObject));
                    httpRequestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                }

                var response = await httpClient.SendAsync(httpRequestMessage);

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Legacy IO request to {url} failed with {response.StatusCode} ({response.Content.ReadAsStringAsync().Result})");

                if ((int)response.StatusCode >= 300)
                    throw new Exception($"Legacy IO request to {url} returned unexpected response {response.StatusCode} ({response.ReasonPhrase})");
            }
            catch (Exception e)
            {
                if (retryCount-- > 0)
                {
                    logger.LogError(e, "Legacy IO request to {url} failed, retrying ({retries} remaining)", url, retryCount);
                    Thread.Sleep(1000);
                    goto retry;
                }

                throw;
            }
        }

        private static string hmacEncode(string input, byte[] key)
        {
            byte[] byteArray = Encoding.ASCII.GetBytes(input);

            using (var hmac = new HMACSHA1(key))
            {
                byte[] hashArray = hmac.ComputeHash(byteArray);
                return hashArray.Aggregate(string.Empty, (s, e) => s + $"{e:x2}", s => s);
            }
        }

        // Methods below purposefully async-await on `runLegacyIO()` calls rather than directly returning the underlying calls.
        // This is done for better readability of exception stacks. Directly returning the tasks elides the name of the proxying method.

        public async Task DisqualifyBeatmapSetAsync(uint beatmapSetId, string message)
            => await runLegacyIO(HttpMethod.Post, $"beatmapsets/{beatmapSetId}/disqualify", new { message = message });

        public async Task BroadcastReviveBeatmapSetEventAsync(uint beatmapSetId)
            => await runLegacyIO(HttpMethod.Post, $"beatmapsets/{beatmapSetId}/broadcast-revive", new { create_event = true });

        public async Task BroadcastNewBeatmapSetEventAsync(uint beatmapSetId)
            => await runLegacyIO(HttpMethod.Post, $"beatmapsets/{beatmapSetId}/broadcast-new", new { create_event = true });

        public async Task BroadcastUpdateBeatmapSetEventAsync(uint beatmapSetId, uint userId)
            => await runLegacyIO(HttpMethod.Post, $"beatmapsets/{beatmapSetId}/broadcast-update", new { user_id = userId });

        public async Task IndexBeatmapSetAsync(uint beatmapSetId)
            => await runLegacyIO(HttpMethod.Post, $"index-beatmapset/{beatmapSetId}");

        public async Task RefreshBeatmapSetCacheAsync(uint beatmapSetId)
            => await runLegacyIO(HttpMethod.Post, $"refresh-beatmapset-cache/{beatmapSetId}");
    }
}
