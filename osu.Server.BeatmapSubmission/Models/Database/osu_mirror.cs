// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// ReSharper disable InconsistentNaming

namespace osu.Server.BeatmapSubmission.Models.Database
{
    public class osu_mirror
    {
        public ushort mirror_id { get; set; }
        public bool enabled { get; set; }
        public string base_url { get; set; } = string.Empty;
        public decimal version { get; set; }
        public bool perform_updates { get; set; }
        public string secret_key { get; set; } = string.Empty;
    }
}
