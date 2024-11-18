// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// ReSharper disable InconsistentNaming

namespace osu.Server.BeatmapSubmission.Models.Database
{
    public class osu_beatmapset_version
    {
        public ulong version_id { get; set; }
        public uint beatmapset_id { get; set; }
        public DateTimeOffset created_at { get; set; }
        public ulong? previous_version_id { get; set; }
    }
}
