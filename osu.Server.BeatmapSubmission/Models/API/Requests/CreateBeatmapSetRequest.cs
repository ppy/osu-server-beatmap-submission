// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace osu.Server.BeatmapSubmission.Models.API.Requests
{
    public class CreateBeatmapSetRequest
    {
        [JsonPropertyName("beatmap_count")]
        [Range(1, 128)]
        public uint BeatmapCount { get; set; }
    }
}
