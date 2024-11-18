// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Server.BeatmapSubmission.Tests.Resources
{
    public static class TestResources
    {
        public static Stream? GetResource(string name)
            => typeof(TestResources).Assembly.GetManifestResourceStream($"{typeof(TestResources).Namespace}.{name}");
    }
}
