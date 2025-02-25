// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel.DataAnnotations;
using osu.Game.Beatmaps;

namespace osu.Server.BeatmapSubmission.Models.Database.Validation
{
    public class RomanisedAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value)
            => value is string s && MetadataUtils.IsRomanised(s);
    }
}
