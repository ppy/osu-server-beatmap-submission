// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// ReSharper disable InconsistentNaming

using System.ComponentModel.DataAnnotations;

namespace osu.Server.BeatmapSubmission.Models.Database
{
    public class beatmapset_version_file
    {
        public ulong file_id { get; set; }
        public ulong version_id { get; set; }

        [MaxLength(500, ErrorMessage = "Path names within the archive cannot exceed 500 characters.")]
        public string filename { get; set; } = string.Empty;
    }
}
