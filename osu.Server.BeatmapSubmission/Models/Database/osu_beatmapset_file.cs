// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// ReSharper disable InconsistentNaming

namespace osu.Server.BeatmapSubmission.Models.Database
{
    public class osu_beatmapset_file
    {
        public ulong file_id { get; set; }
        public byte[] sha2_hash { get; set; } = new byte[32];
        public uint file_size { get; set; }
    }
}
