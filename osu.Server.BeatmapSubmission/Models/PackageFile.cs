// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections;
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

    public class PackageFileEqualityComparer : IEqualityComparer<PackageFile>
    {
        public bool Equals(PackageFile first, PackageFile second)
        {
            return first.File.sha2_hash.SequenceEqual(second.File.sha2_hash)
                   && first.File.file_size == second.File.file_size
                   && first.VersionFile.filename == second.VersionFile.filename;
        }

        public int GetHashCode(PackageFile file) => HashCode.Combine(StructuralComparisons.StructuralEqualityComparer.GetHashCode(file.File.sha2_hash), file.File.file_size, file.VersionFile.filename);
    }
}
