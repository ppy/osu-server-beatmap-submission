// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using osu.Framework.Extensions;
using osu.Game.IO.Archives;
using osu.Server.BeatmapSubmission.Configuration;
using osu.Server.BeatmapSubmission.Models;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace osu.Server.BeatmapSubmission.Services
{
    public class S3BeatmapStorage : IBeatmapStorage
    {
        private const string osz_directory = "osz";
        private const string versioned_file_directory = "beatmap_files";

        private static AmazonS3Client getClient() => new AmazonS3Client(
            new BasicAWSCredentials(AppSettings.S3AccessKey, AppSettings.S3SecretKey),
            new AmazonS3Config
            {
                CacheHttpClient = true,
                HttpClientCacheSize = 32,
                RegionEndpoint = RegionEndpoint.USWest1,
                UseHttp = true,
                ForcePathStyle = true,
                RetryMode = RequestRetryMode.Legacy,
                MaxErrorRetry = 5,
                Timeout = TimeSpan.FromMinutes(1),
            });

        public async Task StoreBeatmapSetAsync(uint beatmapSetId, byte[] beatmapPackage)
        {
            using var client = getClient();

            var stream = new MemoryStream(beatmapPackage);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = AppSettings.S3BucketName,
                Key = getPathToPackage(beatmapSetId),
                Headers =
                {
                    ContentLength = beatmapPackage.Length,
                    ContentType = "application/x-osu-beatmap-archive",
                },
                InputStream = stream,
            });

            // S3 sdk closes that existing stream... for whatever reason...
            stream = new MemoryStream(beatmapPackage);
            using var archiveReader = new ZipArchiveReader(stream);

            foreach (string filename in archiveReader.Filenames)
            {
                var fileStream = archiveReader.GetStream(filename);
                long length = fileStream.Length;
                string sha2 = fileStream.ComputeSHA2Hash();

                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = AppSettings.S3BucketName,
                    Key = getPathToVersionedFile(sha2),
                    Headers =
                    {
                        ContentLength = length,
                    },
                    InputStream = fileStream,
                });
            }
        }

        public async Task ExtractBeatmapSetAsync(uint beatmapSetId, string targetDirectory)
        {
            using var client = getClient();

            using var response = await client.GetObjectAsync(AppSettings.S3BucketName, getPathToPackage(beatmapSetId));
            // S3-provided `HashStream` does not support seeking which `ZipArchiveReader` does not like.
            var memoryStream = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memoryStream);

            using var archiveReader = new ZipArchiveReader(memoryStream);

            foreach (string sourceFilename in archiveReader.Filenames)
            {
                string targetFilename = Path.Combine(targetDirectory, sourceFilename);
                string directoryPart = Path.GetDirectoryName(targetFilename) ?? string.Empty;

                if (!Directory.Exists(directoryPart))
                    Directory.CreateDirectory(directoryPart);

                using var fileStream = archiveReader.GetStream(sourceFilename);
                byte[] fileContent = await fileStream.ReadAllBytesToArrayAsync();
                await File.WriteAllBytesAsync(targetFilename, fileContent);
            }
        }

        public async Task<Stream> PackageBeatmapSetFilesAsync(IEnumerable<PackageFile> files)
        {
            using var client = getClient();
            var memoryStream = new MemoryStream();

            using (var zipWriter = new ZipWriter(memoryStream, BeatmapPackagePatcher.DEFAULT_ZIP_WRITER_OPTIONS))
            {
                foreach (var file in files)
                {
                    using var response = await client.GetObjectAsync(AppSettings.S3BucketName, getPathToVersionedFile(BitConverter.ToString(file.File.sha2_hash).Replace("-", string.Empty).ToLowerInvariant()));
                    zipWriter.Write(file.VersionFile.filename, response.ResponseStream);
                }
            }

            memoryStream.Seek(0, SeekOrigin.Begin);
            return memoryStream;
        }

        private static string getPathToPackage(uint beatmapSetId) => Path.Combine(osz_directory, BeatmapPackageParser.PackageFilenameFor(beatmapSetId));

        private static string getPathToVersionedFile(string sha2) => Path.Combine(versioned_file_directory, sha2);
    }
}
