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
        string Filename,
        string SHA2Hash)
    {
        /// <summary>
        /// The name of the file.
        /// </summary>
        /// <example>"Soleily - Renatus (Deif) [Platter].osu"</example>
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = Filename;

        /// <summary>
        /// The SHA2 hash of the file's contents.
        /// </summary>
        /// <example>"55532bf222233a44849facb762543d94830c6afa0d6054b5344616723f812c8e"</example>
        [JsonPropertyName("sha2_hash")]
        public string SHA2Hash { get; set; } = SHA2Hash;
    }
}
