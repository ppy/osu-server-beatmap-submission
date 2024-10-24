// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;

namespace osu.Server.BeatmapSubmission.Models.API.Responses
{
    public class CreateBeatmapSetResponse
    {
        [JsonPropertyName("beatmapset_id")]
        public uint BeatmapSetId { get; init; }

        [JsonPropertyName("beatmap_ids")]
        public ICollection<uint> BeatmapIds { get; init; } = Array.Empty<uint>();
    }
}
