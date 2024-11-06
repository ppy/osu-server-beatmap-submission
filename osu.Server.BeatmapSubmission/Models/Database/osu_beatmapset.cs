// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// ReSharper disable InconsistentNaming

using osu.Game.Beatmaps;

namespace osu.Server.BeatmapSubmission.Models.Database
{
    public class osu_beatmapset
    {
        public uint beatmapset_id { get; set; }
        public uint user_id { get; set; }
        public uint thread_id { get; set; }
        public string artist { get; set; } = string.Empty;
        public string? artist_unicode { get; set; }
        public string title { get; set; } = string.Empty;
        public string? title_unicode { get; set; }
        public string creator { get; set; } = string.Empty;
        public string source { get; set; } = string.Empty;
        public string tags { get; set; } = string.Empty;
        public bool video { get; set; }
        public bool storyboard { get; set; }
        public bool epilepsy { get; set; }
        public float bpm { get; set; }
        public ushort versions_available { get; set; } = 1;
        public BeatmapOnlineStatus approved { get; set; }
        public uint? approvedby_id { get; set; }
        public DateTimeOffset? approved_date { get; set; }
        public DateTimeOffset? submit_date { get; set; }

        // last_update skipped on purpose

        public string? filename { get; set; }
        public bool active { get; set; } = true;
        public float rating { get; set; }
        public short offset { get; set; }
        public string displaytitle { get; set; } = string.Empty;
        public ushort genre_id { get; set; } = 1;
        public ushort language_id { get; set; } = 1;
        public short star_priority { get; set; }
        public long filesize { get; set; }
        public long? filesize_novideo { get; set; }
        public byte[]? body_hash { get; set; }
        public byte[]? header_hash { get; set; }
        public byte[]? osz2_hash { get; set; }
        public ushort download_disabled { get; set; }
        public string? download_disabled_url { get; set; }
        public DateTimeOffset? thread_icon_date { get; set; }
        public uint favourite_count { get; set; }
        public uint play_count { get; set; }
        public string? difficulty_names { get; set; }
        public DateTimeOffset? cover_updated_at { get; set; }
        public bool discussion_enabled { get; set; }
        public bool discussion_locked { get; set; }

        // deleted_at skipped on purpose

        public int hype { get; set; }
        public int nominations { get; set; }
        public int previous_queue_duration { get; set; }
        public DateTimeOffset? queued_at { get; set; }
        public string? storyboard_hash { get; set; }
        public bool nsfw { get; set; }
        public uint? track_id { get; set; }
        public bool spotlight { get; set; }
        public byte? comment_locked { get; set; }
        public string? eligible_main_rulesets { get; set; }
    }
}
