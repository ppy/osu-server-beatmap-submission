// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Globalization;
using osu.Framework.Extensions;
using osu.Game.IO.Archives;
using osu.Server.BeatmapSubmission.Configuration;
using osu.Server.BeatmapSubmission.Models.API.Responses;

namespace osu.Server.BeatmapSubmission.Services
{
    public class LocalBeatmapStorage : IBeatmapStorage
    {
        public Task StoreBeatmapSetAsync(uint beatmapSetId, byte[] beatmapPackage)
        {
            string path = getPathTo(beatmapSetId);
            return File.WriteAllBytesAsync(path, beatmapPackage);
        }

        public IEnumerable<BeatmapSetFile> ListBeatmapSetFiles(uint beatmapSetId)
        {
            string path = getPathTo(beatmapSetId);
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchiveReader(stream);

            var result = new List<BeatmapSetFile>();

            foreach (string filename in archive.Filenames)
                result.Add(new BeatmapSetFile(filename, archive.GetStream(filename).ComputeSHA2Hash()));

            return result;
        }

        private static string getPathTo(uint beatmapSetId) => Path.Combine(AppSettings.LocalBeatmapStoragePath, beatmapSetId.ToString(CultureInfo.InvariantCulture));
    }
}
