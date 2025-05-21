// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Extensions;
using osu.Game.IO.Archives;
using osu.Server.BeatmapSubmission.Models;
using osu.Server.BeatmapSubmission.Models.Database;
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
                string targetFilename = Path.Combine(tempDirectory.FullName, fileChanged.FileName);
                string directoryPart = Path.GetDirectoryName(targetFilename) ?? string.Empty;
                string filePart = Path.GetFileName(targetFilename);

                if (filePart.Length > 255)
                    throw new InvariantException($"The filename \"{filePart}\" is too long.");

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
            osu_beatmap beatmap,
            IFormFile beatmapContents)
        {
            var tempDirectory = Directory.CreateTempSubdirectory($"bss_{beatmapSetId}");
            await beatmapStorage.ExtractBeatmapSetAsync(beatmapSetId, tempDirectory.FullName);

            if (beatmap.filename == null)
                throw new InvariantException("Could not find the old .osu file for the beatmap being modified.", LogLevel.Warning);

            File.Delete(Path.Combine(tempDirectory.FullName, beatmap.filename));

            string targetFilename = Path.Combine(tempDirectory.FullName, beatmapContents.FileName);
            if (File.Exists(targetFilename))
                throw new InvariantException($"Chosen filename conflicts with another existing file ({beatmapContents.FileName}).");

            using (var file = File.OpenWrite(targetFilename))
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
