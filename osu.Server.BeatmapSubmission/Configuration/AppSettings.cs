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
    }
}
