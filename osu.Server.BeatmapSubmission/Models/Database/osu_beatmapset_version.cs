// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// ReSharper disable InconsistentNaming

namespace osu.Server.BeatmapSubmission.Models.Database
{
    public class osu_beatmapset_version
    {
        public uint beatmapset_id { get; set; }
        public uint version_id { get; set; }
        public DateTimeOffset uploaded_on { get; set; }
    }
}
