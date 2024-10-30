// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;

namespace osu.Server.BeatmapSubmission.Models.API.Responses
{
    public class ListFilesInBeatmapSetResponse
    {
        public IEnumerable<BeatmapSetFile> Files { get; set; } = [];
    }

    public record struct BeatmapSetFile(
        [property: JsonPropertyName("filename")]
        string Filename,
        [property: JsonPropertyName("sha2_hash")]
        string SHA2Hash);
}
