// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Server.BeatmapSubmission
{
    public static class SanityCheckHelpers
    {
        public static bool IncursPathTraversalRisk(string path)
            => path.Contains("../", StringComparison.Ordinal) || path.Contains("..\\", StringComparison.Ordinal) || Path.IsPathRooted(path);
    }
}
