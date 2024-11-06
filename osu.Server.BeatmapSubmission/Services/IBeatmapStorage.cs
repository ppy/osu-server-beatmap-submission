// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Server.BeatmapSubmission.Models.API.Responses;
using osu.Server.BeatmapSubmission.Models.Database;

namespace osu.Server.BeatmapSubmission.Services
{
    public interface IBeatmapStorage
    {
        Task StoreBeatmapSetAsync(uint beatmapSetId, byte[] beatmapPackage);

        IEnumerable<BeatmapSetFile> ListBeatmapSetFiles(uint beatmapSetId);

        Task ExtractBeatmapSetAsync(uint beatmapSetId, string targetDirectory);

        Task<Stream> PackageBeatmapSetFilesAsync(IEnumerable<osu_beatmapset_version_file> files);
    }
}
