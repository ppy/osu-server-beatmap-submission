// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Server.BeatmapSubmission
{
    public class FormatUtils
    {
        public static string HumaniseSize(double sizeBytes)
        {
            string humanisedSize;

            if (sizeBytes < 1024)
                humanisedSize = $@"{sizeBytes}B";
            else if (sizeBytes < 1024 * 1024)
                humanisedSize = $@"{sizeBytes / 1024:#.0}kB";
            else
                humanisedSize = $@"{sizeBytes / 1024 / 1024:#.0}MB";

            return humanisedSize;
        }
    }
}
