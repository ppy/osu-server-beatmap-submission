// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Server.BeatmapSubmission.Models;

namespace osu.Server.BeatmapSubmission.Services
{
    public interface IBeatmapStorage
    {
        Task StoreBeatmapSetAsync(uint beatmapSetId, byte[] beatmapPackage, BeatmapPackageParseResult result);

        Task ExtractBeatmapSetAsync(uint beatmapSetId, string targetDirectory);

        Task<Stream> PackageBeatmapSetFilesAsync(IEnumerable<PackageFile> files);
    }
}
