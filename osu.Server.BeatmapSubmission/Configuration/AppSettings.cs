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

        public static string S3BucketName =>
            Environment.GetEnvironmentVariable("S3_BUCKET_NAME")
            ?? throw new InvalidOperationException("S3_BUCKET_NAME environment variable not set. "
                                                   + "Please set the value of this variable to the name of the bucket to be used for storing beatmaps on S3.");

        public static string? SentryDsn => Environment.GetEnvironmentVariable("SENTRY_DSN");
    }
}
