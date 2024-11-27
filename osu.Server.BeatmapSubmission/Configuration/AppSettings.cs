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
    }
}
