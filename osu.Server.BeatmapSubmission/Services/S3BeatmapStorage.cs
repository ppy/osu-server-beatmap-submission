// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Globalization;
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

        private readonly ILogger<S3BeatmapStorage> logger;
        private readonly AmazonS3Client client;

        public S3BeatmapStorage(ILogger<S3BeatmapStorage> logger)
        {
            this.logger = logger;
            client = new AmazonS3Client(
                new BasicAWSCredentials(AppSettings.S3AccessKey, AppSettings.S3SecretKey),
                new AmazonS3Config
                {
                    RegionEndpoint = RegionEndpoint.USWest1,
                    UseHttp = true,
                    ForcePathStyle = true,
                    RetryMode = RequestRetryMode.Legacy,
                    MaxErrorRetry = 5,
                    Timeout = TimeSpan.FromMinutes(1),
                });
        }

        public async Task StoreBeatmapSetAsync(uint beatmapSetId, byte[] beatmapPackage, BeatmapPackageParseResult result)
        {
            var stream = new MemoryStream(beatmapPackage);
            using var archiveReader = new ZipArchiveReader(stream);

            var allFiles = new List<byte[]>();
            var beatmapFiles = new List<(int beatmapId, byte[] contents)>();

            Dictionary<string, int> beatmapFilenames = result.Files
                                                             .Where(f => f.BeatmapContent != null)
                                                             .ToDictionary(f => f.BeatmapContent!.Filename, f => f.BeatmapContent!.Beatmap.BeatmapInfo.OnlineID);

            foreach (string filename in archiveReader.Filenames)
            {
                byte[] contents = await archiveReader.GetStream(filename).ReadAllBytesToArrayAsync();

                allFiles.Add(contents);
                if (beatmapFilenames.TryGetValue(filename, out int beatmapId))
                    beatmapFiles.Add((beatmapId, contents));
            }

            var tasks = new List<Task> { uploadBeatmapPackage(beatmapSetId, beatmapPackage, stream) };
            tasks.AddRange(uploadAllVersionedFiles(beatmapSetId, allFiles));
            tasks.AddRange(uploadAllBeatmapFiles(beatmapSetId, beatmapFiles));
            await Task.WhenAll(tasks);

            logger.LogInformation("All file uploads for beatmapset {beatmapSetId} concluded successfully.", beatmapSetId);
        }

        private Task<PutObjectResponse> uploadBeatmapPackage(uint beatmapSetId, byte[] beatmapPackage, MemoryStream stream)
        {
            logger.LogInformation("Beginning upload of package for beatmapset {beatmapSetId}...", beatmapSetId);
            return client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = AppSettings.S3CentralBucketName,
                Key = getPathToPackage(beatmapSetId),
                Headers =
                {
                    ContentLength = beatmapPackage.Length,
                    ContentType = "application/x-osu-beatmap-archive",
                },
                InputStream = stream,
                CannedACL = S3CannedACL.Private,
            });
        }

        private IEnumerable<Task> uploadAllVersionedFiles(uint beatmapSetId, List<byte[]> files)
        {
            logger.LogInformation("Beginning upload of all versioned files for beatmapset {beatmapSetId}...", beatmapSetId);
            return files.Select(file => Task.Run(async () =>
            {
                var fileStream = new MemoryStream(file);
                long length = file.Length;
                string sha2 = fileStream.ComputeSHA2Hash();

                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = AppSettings.S3CentralBucketName,
                    Key = getPathToVersionedFile(sha2),
                    Headers =
                    {
                        ContentLength = length,
                    },
                    InputStream = fileStream,
                    CannedACL = S3CannedACL.Private,
                });
            }));
        }

        private IEnumerable<Task> uploadAllBeatmapFiles(uint beatmapSetId, List<(int beatmapId, byte[] contents)> beatmapFiles)
        {
            logger.LogInformation("Beginning upload of all .osu beatmap files for beatmapset {beatmapSetId}...", beatmapSetId);
            return beatmapFiles.Select(file => Task.Run(async () =>
            {
                var fileStream = new MemoryStream(file.contents);
                long length = fileStream.Length;

                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = AppSettings.S3BeatmapsBucketName,
                    Key = getPathToBeatmapFile(file.beatmapId),
                    Headers =
                    {
                        ContentLength = length,
                        ContentType = "text/plain",
                    },
                    InputStream = fileStream,
                    CannedACL = S3CannedACL.Private,
                });
            }));
        }

        public async Task ExtractBeatmapSetAsync(uint beatmapSetId, string targetDirectory)
        {
            logger.LogInformation("Retrieving package for beatmap set {beatmapSetId}", beatmapSetId);
            using var response = await client.GetObjectAsync(AppSettings.S3CentralBucketName, getPathToPackage(beatmapSetId));
            logger.LogInformation("Package for beatmap set {beatmapSetId} retrieved successfully.", beatmapSetId);
            // S3-provided `HashStream` does not support seeking which `ZipArchiveReader` does not like.
            var memoryStream = new MemoryStream(await response.ResponseStream.ReadAllRemainingBytesToArrayAsync());

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
            var memoryStream = new MemoryStream();

            logger.LogInformation("Retrieving requested files...");

            using (var zipWriter = new ZipWriter(memoryStream, BeatmapPackagePatcher.DEFAULT_ZIP_WRITER_OPTIONS))
            {
                foreach (var file in files)
                {
                    using var response = await client.GetObjectAsync(AppSettings.S3CentralBucketName,
                        getPathToVersionedFile(BitConverter.ToString(file.File.sha2_hash).Replace("-", string.Empty).ToLowerInvariant()));
                    zipWriter.Write(file.VersionFile.filename, response.ResponseStream);
                }
            }

            logger.LogInformation("Package files retrieved successfully.");

            memoryStream.Seek(0, SeekOrigin.Begin);
            return memoryStream;
        }

        private static string getPathToPackage(uint beatmapSetId) => Path.Combine(osz_directory, BeatmapPackageParser.PackageFilenameFor(beatmapSetId));

        private static string getPathToVersionedFile(string sha2) => Path.Combine(versioned_file_directory, sha2);

        private static string getPathToBeatmapFile(int beatmapId) => beatmapId.ToString(CultureInfo.InvariantCulture);
    }
}
