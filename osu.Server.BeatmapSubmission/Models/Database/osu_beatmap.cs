// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// ReSharper disable InconsistentNaming

using System.ComponentModel.DataAnnotations;
using osu.Game.Beatmaps;
using osu.Server.BeatmapSubmission.Models.Database.Validation;

namespace osu.Server.BeatmapSubmission.Models.Database
{
    public class osu_beatmap
    {
        public uint beatmap_id { get; set; }
        public uint? beatmapset_id { get; set; }
        public uint user_id { get; set; }

        [MaxLength(150, ErrorMessage = "Beatmap difficulty filenames must not exceed 150 characters.")]
        public string? filename { get; set; }

        public string? checksum { get; set; }

        [MaxLength(80, ErrorMessage = "Beatmap difficulty names must not exceed 80 characters.")]
        [Romanised(ErrorMessage = "Difficulty name contains disallowed characters.")]
        public string version { get; set; } = string.Empty;

        public uint total_length { get; set; }
        public uint hit_length { get; set; }
        public uint countTotal { get; set; }
        public uint countNormal { get; set; }
        public uint countSlider { get; set; }
        public uint countSpinner { get; set; }
        public float diff_drain { get; set; }
        public float diff_size { get; set; }
        public float diff_overall { get; set; }
        public float diff_approach { get; set; }
        public ushort playmode { get; set; }
        public BeatmapOnlineStatus approved { get; set; }

        // last_update skipped on purpose

        public float difficultyrating { get; set; }
        public uint max_combo { get; set; }
        public uint playcount { get; set; }
        public uint passcount { get; set; }
        public string? youtube_preview { get; set; }
        public ushort score_version { get; set; }

        // deleted_at skipped on purpose

        public float bpm { get; set; }
    }
}
