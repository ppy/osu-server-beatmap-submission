// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Security.Cryptography;
using osu.Framework.Extensions;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Beatmaps.Legacy;
using osu.Game.IO;
using osu.Game.IO.Archives;
using osu.Game.Rulesets.Objects.Legacy;
using osu.Game.Storyboards;
using osu.Server.BeatmapSubmission.Models;
using osu.Server.BeatmapSubmission.Models.Database;

namespace osu.Server.BeatmapSubmission.Services
{
    public static class BeatmapPackageParser
    {
        public static BeatmapPackageParseResult Parse(uint beatmapSetId, ArchiveReader archiveReader)
        {
            string[] filenames = archiveReader.Filenames.ToArray();

            BeatmapContent[] beatmaps = getBeatmapContent(archiveReader, filenames).ToArray();

            var files = new List<VersionedFile>(filenames.Length);

            foreach (string filename in filenames)
            {
                var stream = archiveReader.GetStream(filename);
                files.Add(new VersionedFile(
                    new osu_beatmapset_file
                    {
                        sha2_hash = SHA256.HashData(stream),
                        file_size = (uint)stream.Length,
                    },
                    new osu_beatmapset_version_file
                    {
                        filename = filename,
                    }
                ));
            }

            // TODO: ensure the beatmaps have correct online IDs inside

            osu_beatmap[] beatmapRows = beatmaps.Select(constructDatabaseRowForBeatmap).ToArray();
            var beatmapSetRow = constructDatabaseRowForBeatmapset(beatmapSetId, archiveReader, beatmaps);

            return new BeatmapPackageParseResult(beatmapSetRow, beatmapRows, files.ToArray());
        }

        private static IEnumerable<BeatmapContent> getBeatmapContent(ArchiveReader archiveReader, string[] filenames)
        {
            foreach (string file in filenames.Where(filename => Path.GetExtension(filename).Equals(".osu", StringComparison.OrdinalIgnoreCase)))
            {
                using var contents = archiveReader.GetStream(file);
                var decoder = new LegacyBeatmapDecoder();
                var beatmap = decoder.Decode(new LineBufferedReader(contents));

                yield return new BeatmapContent(Path.GetFileName(file), contents.ComputeMD5Hash(), beatmap);
            }
        }

        private static osu_beatmap constructDatabaseRowForBeatmap(BeatmapContent beatmapContent)
        {
            float beatLength = (float)beatmapContent.Beatmap.GetMostCommonBeatLength();

            var result = new osu_beatmap
            {
                beatmap_id = (uint)beatmapContent.Beatmap.BeatmapInfo.OnlineID,
                filename = beatmapContent.Filename,
                checksum = beatmapContent.MD5,
                version = beatmapContent.Beatmap.BeatmapInfo.DifficultyName,
                diff_drain = beatmapContent.Beatmap.Difficulty.DrainRate,
                diff_size = beatmapContent.Beatmap.Difficulty.CircleSize,
                diff_overall = beatmapContent.Beatmap.Difficulty.OverallDifficulty,
                diff_approach = beatmapContent.Beatmap.Difficulty.ApproachRate,
                bpm = beatLength > 0 ? 60000 / beatLength : 0,
                total_length = (uint)(beatmapContent.Beatmap.CalculatePlayableLength() / 1000),
                hit_length = (uint)(beatmapContent.Beatmap.CalculateDrainLength() / 1000),
                playmode = (ushort)beatmapContent.Beatmap.BeatmapInfo.Ruleset.OnlineID,
            };

            countObjectsByType(beatmapContent.Beatmap, result);
            return result;
        }

        private static osu_beatmapset constructDatabaseRowForBeatmapset(uint beatmapSetId, ArchiveReader archiveReader, BeatmapContent[] beatmaps)
        {
            // TODO: currently all exceptions thrown here will be 500s, they should be 429s

            if (beatmaps.Length == 0)
                throw new InvalidOperationException("The uploaded beatmap set must have at least one difficulty.");

            float firstBeatLength = (float)beatmaps.First().Beatmap.GetMostCommonBeatLength();

            var result = new osu_beatmapset
            {
                beatmapset_id = beatmapSetId,
                artist = getSingleValueFrom(beatmaps, c => c.Beatmap.Metadata.Artist, nameof(BeatmapMetadata.Artist)),
                artist_unicode = getSingleValueFrom(beatmaps,
                    c => !string.IsNullOrEmpty(c.Beatmap.Metadata.ArtistUnicode) ? c.Beatmap.Metadata.ArtistUnicode : null,
                    nameof(BeatmapMetadata.ArtistUnicode)),
                title = getSingleValueFrom(beatmaps, c => c.Beatmap.Metadata.Title, nameof(BeatmapMetadata.Title)),
                title_unicode = getSingleValueFrom(beatmaps,
                    c => !string.IsNullOrEmpty(c.Beatmap.Metadata.TitleUnicode) ? c.Beatmap.Metadata.TitleUnicode : null,
                    nameof(BeatmapMetadata.TitleUnicode)),
                source = getSingleValueFrom(beatmaps, c => c.Beatmap.Metadata.Source, nameof(BeatmapMetadata.Source)),
                tags = getSingleValueFrom(beatmaps, c => c.Beatmap.Metadata.Tags, nameof(BeatmapMetadata.Tags)),
                bpm = firstBeatLength > 0 ? 60000 / firstBeatLength : 0,
                filename = FormattableString.Invariant($"{beatmapSetId}.osz"),
            };

            // TODO: maybe unnecessary?
            result.displaytitle = $"[bold:0,size:20]{result.artist_unicode}|{result.title_unicode}";

            // TODO: not updating `difficulty_names` despite original BSS doing so - pretty sure that cannot work correctly in the original BSS either
            // (old BSS embeds star ratings into the value of that which are not going to be correct at this point in time).

            if (archiveReader.Filenames.Any(f => OsuGameBase.VIDEO_EXTENSIONS.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                result.video = true;
            }

            if (archiveReader.Filenames.FirstOrDefault(f => Path.GetExtension(f).Equals(".osb", StringComparison.OrdinalIgnoreCase)) is string storyboardFileName)
            {
                using var storyboardStream = archiveReader.GetStream(storyboardFileName);
                var storyboardDecoder = new LegacyStoryboardDecoder();
                var storyboard = storyboardDecoder.Decode(new LineBufferedReader(storyboardStream));

                if (storyboard.Layers.Any(l => l.Elements.Any(elem => elem.GetType() == typeof(StoryboardSprite) || elem.GetType() == typeof(StoryboardAnimation))))
                {
                    result.storyboard = true;
                    result.storyboard_hash = storyboardStream.ComputeMD5Hash();
                }
            }

            return result;
        }

        private static T getSingleValueFrom<T>(IEnumerable<BeatmapContent> beatmapContents, Func<BeatmapContent, T> accessor, string valueName)
        {
            T[] distinctValues = beatmapContents.Select(accessor).Distinct().ToArray();
            if (distinctValues.Length != 1)
                throw new InvalidOperationException($"The uploaded beatmap set's individual difficulties have inconsistent {valueName}. Please unify {valueName} before re-submitting.");

            return distinctValues.Single();
        }

        private static void countObjectsByType(Beatmap beatmap, osu_beatmap result)
        {
            foreach (var hitobject in beatmap.HitObjects.OfType<IHasLegacyHitObjectType>())
            {
                if ((hitobject.LegacyType & LegacyHitObjectType.Circle) > 0)
                    result.countNormal += 1;
                if ((hitobject.LegacyType & LegacyHitObjectType.Slider) > 0 || (hitobject.LegacyType & LegacyHitObjectType.Hold) > 0)
                    result.countSlider += 1;
                if ((hitobject.LegacyType & LegacyHitObjectType.Spinner) > 0)
                    result.countSpinner += 1;
                result.countTotal += 1;
            }
        }

        private record BeatmapContent(string Filename, string MD5, Beatmap Beatmap);
    }

    public record struct BeatmapPackageParseResult(
        osu_beatmapset BeatmapSet,
        osu_beatmap[] Beatmaps,
        VersionedFile[] Files);
}
