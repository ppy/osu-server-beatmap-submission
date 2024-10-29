// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Globalization;
using osu.Server.BeatmapSubmission.Configuration;

namespace osu.Server.BeatmapSubmission.Services
{
    public class LocalBeatmapStorage : IBeatmapStorage
    {
        public Task StoreBeatmapSetAsync(uint beatmapSetId, byte[] beatmapPackage)
        {
            string path = Path.Combine(AppSettings.LocalBeatmapStoragePath, beatmapSetId.ToString(CultureInfo.InvariantCulture));
            return File.WriteAllBytesAsync(path, beatmapPackage);
        }
    }
}
