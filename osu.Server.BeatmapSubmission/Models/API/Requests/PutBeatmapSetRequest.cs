// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;

namespace osu.Server.BeatmapSubmission.Models.API.Requests
{
    public class PutBeatmapSetRequest
    {
        /// <summary>
        /// If not <see langword="null"/>, indicates an existing beatmap set to update.
        /// If <see langword="null"/>, this request will create a brand new set.
        /// </summary>
        /// <example>241526</example>
        [JsonPropertyName("beatmapset_id")]
        public uint? BeatmapSetID { get; set; }

        /// <summary>
        /// The number of new beatmaps to create in the set.
        /// Should be used when submitting a new beatmap (difficulty) for the first time.
        /// </summary>
        /// <example>12</example>
        [JsonPropertyName("beatmaps_to_create")]
        public uint BeatmapsToCreate { get; set; }

        /// <summary>
        /// The IDs of existing beatmaps to keep in the set.
        /// Should be used when updating an existing beatmap.
        /// Any beatmaps previously associated with the set will be deleted if they are not present in this array.
        /// </summary>
        /// <example>[841629, 841658, 874240, 838103]</example>
        [JsonPropertyName("beatmaps_to_keep")]
        public uint[] BeatmapsToKeep { get; set; } = [];
    }
}
