// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Server.BeatmapSubmission.Models.Database;
using osu.Server.BeatmapSubmission.Services;

namespace osu.Server.BeatmapSubmission.Models
{
    public readonly struct PackageFile(beatmapset_file file, beatmapset_version_file versionFile, BeatmapContent? beatmapContent = null)
    {
        public beatmapset_file File { get; } = file;
        public beatmapset_version_file VersionFile { get; } = versionFile;
        public BeatmapContent? BeatmapContent { get; } = beatmapContent;
    }
}
