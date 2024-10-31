// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Extensions;
using osu.Game.IO.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace osu.Server.BeatmapSubmission.Services
{
    public class BeatmapPackagePatcher
    {
        private readonly IBeatmapStorage beatmapStorage;

        public BeatmapPackagePatcher(IBeatmapStorage beatmapStorage)
        {
            this.beatmapStorage = beatmapStorage;
        }

        public async Task<MemoryStream> PatchAsync(
            uint beatmapSetId,
            IEnumerable<IFormFile> filesChanged,
            IEnumerable<string> filesDeleted)
        {
            var tempDirectory = Directory.CreateTempSubdirectory($"bss_{beatmapSetId}");
            await beatmapStorage.ExtractBeatmapSetAsync(beatmapSetId, tempDirectory.FullName);

            foreach (var fileChanged in filesChanged)
            {
                // TODO: here's an interesting question, how to handle files in subdirectories?
                // i'm hoping this will "just work" out of the box, but it needs to be tested
                string targetFilename = Path.Combine(tempDirectory.FullName, fileChanged.FileName);
                string directoryPart = Path.GetDirectoryName(targetFilename) ?? string.Empty;

                if (!Directory.Exists(directoryPart))
                    Directory.CreateDirectory(directoryPart);

                using var fileStream = fileChanged.OpenReadStream();
                await File.WriteAllBytesAsync(targetFilename, await fileStream.ReadAllBytesToArrayAsync());
            }

            foreach (string fileDeleted in filesDeleted)
            {
                string targetFilename = Path.Combine(tempDirectory.FullName, fileDeleted);

                if (File.Exists(targetFilename))
                    File.Delete(targetFilename);
            }

            var zipWriterOptions = new ZipWriterOptions(CompressionType.Deflate)
            {
                ArchiveEncoding = ZipArchiveReader.DEFAULT_ENCODING,
            };
            var beatmapStream = new MemoryStream();

            using (var writer = new ZipWriter(beatmapStream, zipWriterOptions))
            {
                foreach (string file in Directory.EnumerateFiles(tempDirectory.FullName, "*", SearchOption.AllDirectories))
                {
                    using var stream = File.OpenRead(file);
                    writer.Write(Path.GetRelativePath(tempDirectory.FullName, file), stream);
                }
            }

            Directory.Delete(tempDirectory.FullName, true);

            return beatmapStream;
        }
    }
}
