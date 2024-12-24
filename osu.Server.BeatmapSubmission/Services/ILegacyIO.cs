// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Server.BeatmapSubmission.Services
{
    public interface ILegacyIO
    {
        Task DisqualifyBeatmapSetAsync(uint beatmapSetId, string message);
        Task BroadcastReviveBeatmapSetEventAsync(uint beatmapSetId);
        Task BroadcastNewBeatmapSetEventAsync(uint beatmapSetId);
        Task BroadcastUpdateBeatmapSetEventAsync(uint beatmapSetId, uint userId);
        Task IndexBeatmapSetAsync(uint beatmapSetId);
        Task RefreshBeatmapSetCacheAsync(uint beatmapSetId);
    }
}
