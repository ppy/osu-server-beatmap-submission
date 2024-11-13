// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Globalization;
using osu.Framework.Extensions;
using osu.Game.IO.Archives;
using osu.Server.BeatmapSubmission.Configuration;
using osu.Server.BeatmapSubmission.Models.API.Responses;
using osu.Server.BeatmapSubmission.Models.Database;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace osu.Server.BeatmapSubmission.Services
{
    public class LocalBeatmapStorage : IBeatmapStorage
    {
        public string BaseDirectory { get; }

        public LocalBeatmapStorage(string? directory = null)
        {
            BaseDirectory = directory ?? AppSettings.LocalBeatmapStoragePath;
        }

        public async Task StoreBeatmapSetAsync(uint beatmapSetId, byte[] beatmapPackage)
        {
            string path = getPathToPackage(beatmapSetId);
            await File.WriteAllBytesAsync(path, beatmapPackage);

            var stream = new MemoryStream(beatmapPackage);
            using var archive = new ZipArchiveReader(stream);

            foreach (string filename in archive.Filenames)
            {
                var sourceStream = archive.GetStream(filename);
                string sha2 = sourceStream.ComputeSHA2Hash();
                string targetPath = getPathToVersionedFile(beatmapSetId, sha2);
                string targetDirectory = Path.GetDirectoryName(targetPath)!;

                if (!Directory.Exists(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                using var targetStream = File.OpenWrite(targetPath);
                await sourceStream.CopyToAsync(targetStream);
            }
        }

        public IEnumerable<BeatmapSetFile> ListBeatmapSetFiles(uint beatmapSetId)
        {
            string path = getPathToPackage(beatmapSetId);

            if (!File.Exists(path))
                return [];

            using var stream = File.OpenRead(path);
            using var archive = new ZipArchiveReader(stream);

            var result = new List<BeatmapSetFile>();

            foreach (string filename in archive.Filenames)
                result.Add(new BeatmapSetFile(filename, archive.GetStream(filename).ComputeSHA2Hash()));

            return result;
        }

        public async Task ExtractBeatmapSetAsync(uint beatmapSetId, string targetDirectory)
        {
            string archivePath = getPathToPackage(beatmapSetId);
            using var archiveStream = File.OpenRead(archivePath);
            using var archive = new ZipArchiveReader(archiveStream);

            foreach (string sourceFilename in archive.Filenames)
            {
                string targetFilename = Path.Combine(targetDirectory, sourceFilename);
                string directoryPart = Path.GetDirectoryName(targetFilename) ?? string.Empty;

                if (!Directory.Exists(directoryPart))
                    Directory.CreateDirectory(directoryPart);

                using var fileStream = archive.GetStream(sourceFilename);
                byte[] fileContent = await fileStream.ReadAllBytesToArrayAsync();
                await File.WriteAllBytesAsync(targetFilename, fileContent);
            }
        }

        public Task<Stream> PackageBeatmapSetFilesAsync(IEnumerable<osu_beatmapset_version_file> files) => Task.Run<Stream>(() =>
        {
            var memoryStream = new MemoryStream();

            using (var zipWriter = new ZipWriter(memoryStream, BeatmapPackagePatcher.DEFAULT_ZIP_WRITER_OPTIONS))
            {
                foreach (var file in files)
                {
                    string targetFilename = getPathToVersionedFile(file.beatmapset_id, BitConverter.ToString(file.sha2_hash).Replace("-", string.Empty).ToLowerInvariant());

                    using var fileStream = File.OpenRead(targetFilename);
                    zipWriter.Write(file.filename, fileStream);
                }
            }

            memoryStream.Seek(0, SeekOrigin.Begin);
            return memoryStream;
        });

        private string getPathToPackage(uint beatmapSetId) => Path.Combine(BaseDirectory, beatmapSetId.ToString(CultureInfo.InvariantCulture));

        private string getPathToVersionedFile(uint beatmapSetId, string sha2)
            => Path.Combine(BaseDirectory, FormattableString.Invariant($@"{beatmapSetId}_files"), sha2);
    }
}
