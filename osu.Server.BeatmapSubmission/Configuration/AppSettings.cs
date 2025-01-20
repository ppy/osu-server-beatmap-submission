// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Server.BeatmapSubmission.Configuration
{
    public static class AppSettings
    {
        public static string JwtValidAudience =>
            Environment.GetEnvironmentVariable("JWT_VALID_AUDIENCE")
            ?? throw new InvalidOperationException("JWT_VALID_AUDIENCE environment variable not set. "
                                                   + "The variable is used to authenticate clients using JWTs issued by osu-web. "
                                                   + "Please set the value of this variable to the client ID assigned to osu! in the osu-web target deploy.");

        public static StorageType StorageType
        {
            get
            {
                string? value = Environment.GetEnvironmentVariable("STORAGE_TYPE");

                if (!Enum.TryParse(value, true, out StorageType storageType) || !Enum.IsDefined(storageType))
                {
                    throw new InvalidOperationException($"STORAGE_TYPE environment variable not set to a valid value (`{value}`). "
                                                        + "The variable is used to choose the implementation of beatmap storage used. "
                                                        + "Valid values are:\n"
                                                        + "- `local` (requires setting `LOCAL_BEATMAP_STORAGE_PATH`),\n"
                                                        + "- `s3` (requires setting `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_CENTRAL_BUCKET_NAME`, `S3_BEATMAPS_BUCKET_NAME`)");
                }

                return storageType;
            }
        }

        public static string LocalBeatmapStoragePath =>
            Environment.GetEnvironmentVariable("LOCAL_BEATMAP_STORAGE_PATH")
            ?? throw new InvalidOperationException("LOCAL_BEATMAP_STORAGE_PATH environment variable not set. "
                                                   + "Please set the value of this variable to the path of a directory where the submitted beatmaps should reside.");

        public static string LegacyIODomain =>
            Environment.GetEnvironmentVariable("LEGACY_IO_DOMAIN")
            ?? throw new InvalidOperationException("LEGACY_IO_DOMAIN environment variable not set. "
                                                   + "Please set the value of this variable to the root URL of the osu-web instance to which legacy IO call should be submitted.");

        public static string SharedInteropSecret =>
            Environment.GetEnvironmentVariable("SHARED_INTEROP_SECRET")
            ?? throw new InvalidOperationException("SHARED_INTEROP_SECRET environment variable not set. "
                                                   + "Please set the value of this variable to the value of the same environment variable that the target osu-web instance specifies in `.env`.");

        public static string S3AccessKey =>
            Environment.GetEnvironmentVariable("S3_ACCESS_KEY")
            ?? throw new InvalidOperationException("S3_ACCESS_KEY environment variable not set. "
                                                   + "Please set the value of this variable to a valid Amazon S3 access key ID.");

        public static string S3SecretKey =>
            Environment.GetEnvironmentVariable("S3_SECRET_KEY")
            ?? throw new InvalidOperationException("S3_SECRET_KEY environment variable not set. "
                                                   + "Please set the value of this variable to the correct secret key for the S3_ACCESS_KEY supplied.");

        public static string S3CentralBucketName =>
            Environment.GetEnvironmentVariable("S3_CENTRAL_BUCKET_NAME")
            ?? throw new InvalidOperationException("S3_CENTRAL_BUCKET_NAME environment variable not set. "
                                                   + "Please set the value of this variable to the name of the bucket to be used for storing beatmap packages and versioned files on S3.");

        public static string S3BeatmapsBucketName =>
            Environment.GetEnvironmentVariable("S3_BEATMAPS_BUCKET_NAME")
            ?? throw new InvalidOperationException("S3_BEATMAPS_BUCKET_NAME environment variable not set. "
                                                   + "Please set the value of this variable to the name of the bucket to be used for storing .osu beatmap files on S3.");

        public static bool PurgeBeatmapMirrorCaches => Environment.GetEnvironmentVariable("PURGE_BEATMAP_MIRROR_CACHES") != "0";

        public static string? SentryDsn => Environment.GetEnvironmentVariable("SENTRY_DSN");

        public static string? DatadogAgentHost => Environment.GetEnvironmentVariable("DD_AGENT_HOST");
    }

    public enum StorageType
    {
        Local,
        S3,
    }
}
