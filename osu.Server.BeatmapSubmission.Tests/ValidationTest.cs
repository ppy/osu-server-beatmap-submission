// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel.DataAnnotations;
using osu.Server.BeatmapSubmission.Models.Database;

namespace osu.Server.BeatmapSubmission.Tests
{
    public class ValidationTest
    {
        [Fact]
        public void TestValidationRange()
        {
            var beatmap = new osu_beatmap
            {
                beatmap_id = 100000,
                filename = "100000.osu",
                checksum = "deadbeef",
                version = "a version",
                diff_drain = 5,
                diff_size = 5,
                diff_overall = 5,
                diff_approach = 5,
                bpm = 120,
                total_length = (uint)int.MaxValue + 1,
                hit_length = (uint)int.MaxValue + 1,
                playmode = 1,
            };

            var errors = new List<ValidationResult>();
            bool success = Validator.TryValidateObject(beatmap, new ValidationContext(beatmap), errors, validateAllProperties: true);

            Assert.False(success);
        }
    }
}
