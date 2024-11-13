// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;
using osu.Server.BeatmapSubmission.Models.API.Requests;

namespace osu.Server.BeatmapSubmission.Models.API.Responses
{
    public class PutBeatmapSetResponse
    {
        /// <summary>
        /// The ID of the beatmap set affected by the operation.
        /// Matches <see cref="PutBeatmapSetRequest.BeatmapSetID"/> if it wasn't <c>null</c>,
        /// or contains the ID of the newly created set if it was.
        /// </summary>
        /// <example>241526</example>
        [JsonPropertyName("beatmapset_id")]
        public uint BeatmapSetId { get; init; }

        /// <summary>
        /// The IDs of all beatmaps associated with the set after the operation.
        /// </summary>
        /// <example>[841629, 841658, 874240, 838103]</example>
        [JsonPropertyName("beatmap_ids")]
        public ICollection<uint> BeatmapIds { get; init; } = Array.Empty<uint>();

        /// <summary>
        /// The list of all files in the latest <c>.osz</c> package uploaded for this beatmap set.
        /// </summary>
        [JsonPropertyName("files")]
        public IEnumerable<BeatmapSetFile> Files { get; set; } = Array.Empty<BeatmapSetFile>();
    }
}
