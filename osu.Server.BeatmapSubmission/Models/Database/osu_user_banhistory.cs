// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// ReSharper disable InconsistentNaming

namespace osu.Server.BeatmapSubmission.Models.Database
{
    public class osu_user_banhistory
    {
        public uint ban_id { get; set; }
        public uint user_id { get; set; }
        public uint period { get; set; }
        public DateTimeOffset timestamp { get; set; }

        /// <seealso href="https://github.com/ppy/osu-web/blob/65ca10d9b137009c5a33876b4caef3453dfb0bc2/app/Models/UserAccountHistory.php#L113-L116"/>
        public DateTimeOffset EndTime => timestamp.AddSeconds(period);
    }
}
