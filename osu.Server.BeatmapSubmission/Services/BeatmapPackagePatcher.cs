// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Extensions;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.IO.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace osu.Server.BeatmapSubmission.Services
{
    public class BeatmapPackagePatcher
    {
        public static readonly ZipWriterOptions DEFAULT_ZIP_WRITER_OPTIONS = new ZipWriterOptions(CompressionType.Deflate)
        {
            ArchiveEncoding = ZipArchiveReader.DEFAULT_ENCODING,
        };

        private readonly IBeatmapStorage beatmapStorage;

        public BeatmapPackagePatcher(IBeatmapStorage beatmapStorage)
        {
            this.beatmapStorage = beatmapStorage;
        }

        public async Task<MemoryStream> PatchBeatmapSetAsync(
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

            var archiveStream = createOszArchive(tempDirectory);
            Directory.Delete(tempDirectory.FullName, true);
            return archiveStream;
        }

        private static MemoryStream createOszArchive(DirectoryInfo tempDirectory)
        {
            var archiveStream = new MemoryStream();

            using (var writer = new ZipWriter(archiveStream, DEFAULT_ZIP_WRITER_OPTIONS))
            {
                foreach (string file in Directory.EnumerateFiles(tempDirectory.FullName, "*", SearchOption.AllDirectories))
                {
                    using var stream = File.OpenRead(file);
                    writer.Write(Path.GetRelativePath(tempDirectory.FullName, file), stream);
                }
            }

            return archiveStream;
        }

        public async Task<MemoryStream> PatchBeatmapAsync(
            uint beatmapSetId,
            uint beatmapId,
            IFormFile beatmapContents)
        {
            var tempDirectory = Directory.CreateTempSubdirectory($"bss_{beatmapSetId}");
            await beatmapStorage.ExtractBeatmapSetAsync(beatmapSetId, tempDirectory.FullName);

            string? existingBeatmapFilename = null;

            foreach (string file in Directory.EnumerateFiles(tempDirectory.FullName, "*.osu", SearchOption.AllDirectories))
            {
                using var stream = File.OpenRead(file);
                var decoded = new LegacyBeatmapDecoder().Decode(new LineBufferedReader(stream));

                if (decoded.BeatmapInfo.OnlineID == beatmapId)
                {
                    existingBeatmapFilename = file;
                    break;
                }
            }

            if (existingBeatmapFilename == null)
                throw new InvalidOperationException("Could not find the old .osu file for the beatmap being modified!");

            File.Delete(existingBeatmapFilename);

            using (var file = File.OpenWrite(Path.Combine(tempDirectory.FullName, beatmapContents.FileName)))
            {
                using var beatmapStream = beatmapContents.OpenReadStream();
                await beatmapStream.CopyToAsync(file);
            }

            var archiveStream = createOszArchive(tempDirectory);
            Directory.Delete(tempDirectory.FullName, true);
            return archiveStream;
        }
    }
}
