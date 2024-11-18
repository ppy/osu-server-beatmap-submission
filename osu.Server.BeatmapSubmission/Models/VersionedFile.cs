// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Server.BeatmapSubmission.Models.Database;

namespace osu.Server.BeatmapSubmission.Models
{
    public readonly struct VersionedFile(osu_beatmapset_file file, osu_beatmapset_version_file versionFile)
    {
        public osu_beatmapset_file File { get; } = file;
        public osu_beatmapset_version_file VersionFile { get; } = versionFile;
    }
}
