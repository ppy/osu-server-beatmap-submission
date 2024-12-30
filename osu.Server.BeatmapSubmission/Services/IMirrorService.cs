// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using MySqlConnector;

namespace osu.Server.BeatmapSubmission.Services
{
    public interface IMirrorService
    {
        Task PurgeBeatmapSetAsync(MySqlConnection db, uint beatmapSetId);
    }
}
