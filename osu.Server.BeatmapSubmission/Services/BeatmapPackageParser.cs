// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Security.Cryptography;
using osu.Framework.Extensions;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Beatmaps.Legacy;
using osu.Game.IO;
using osu.Game.IO.Archives;
using osu.Game.Rulesets.Objects.Legacy;
using osu.Game.Storyboards;
using osu.Game.Utils;
using osu.Server.BeatmapSubmission.Models;
using osu.Server.BeatmapSubmission.Models.Database;

namespace osu.Server.BeatmapSubmission.Services
{
    public static class BeatmapPackageParser
    {
        public static readonly HashSet<string> VALID_EXTENSIONS = new HashSet<string>([..SupportedExtensions.ALL_EXTENSIONS, @".osu"], StringComparer.OrdinalIgnoreCase);

        public static BeatmapPackageParseResult Parse(uint beatmapSetId, ArchiveReader archiveReader)
        {
            string[] filenames = archiveReader.Filenames.ToArray();

            var files = new List<PackageFile>(filenames.Length);

            foreach (string filename in filenames)
            {
                string extension = Path.GetExtension(filename);
                if (!VALID_EXTENSIONS.Contains(extension))
                    throw new InvariantException($"Beatmap contains an unsupported file type ({extension})");

                if (SanityCheckHelpers.IncursPathTraversalRisk(filename))
                    throw new InvariantException("Invalid filename detected");

                var stream = archiveReader.GetStream(filename);
                BeatmapContent? beatmapContent = null;

                if (extension.Equals(".osu", StringComparison.OrdinalIgnoreCase))
                {
                    beatmapContent = getBeatmapContent(filename, stream);

                    if (beatmapContent.Beatmap.BeatmapInfo.BeatmapSet!.OnlineID != beatmapSetId)
                        throw new InvariantException($"Beatmap has invalid beatmap set ID inside ({filename})");
                }

                files.Add(new PackageFile(
                    new beatmapset_file
                    {
                        sha2_hash = SHA256.HashData(stream),
                        file_size = (uint)stream.Length,
                    },
                    new beatmapset_version_file
                    {
                        filename = filename,
                    },
                    beatmapContent
                ));
            }

            var beatmapSetRow = constructDatabaseRowForBeatmapset(beatmapSetId, archiveReader,
                files.Select(f => f.BeatmapContent).Where(c => c != null).ToArray()!);

            return new BeatmapPackageParseResult(beatmapSetRow, files.ToArray());
        }

        private static BeatmapContent getBeatmapContent(string filePath, Stream contents)
        {
            var decoder = new LegacyBeatmapDecoder();
            var beatmap = decoder.Decode(new LineBufferedReader(contents));

            return new BeatmapContent(Path.GetFileName(filePath), contents.ComputeMD5Hash(), beatmap);
        }

        private static osu_beatmapset constructDatabaseRowForBeatmapset(uint beatmapSetId, ArchiveReader archiveReader, ICollection<BeatmapContent> beatmaps)
        {
            if (beatmaps.Count == 0)
                throw new InvariantException("The uploaded beatmap set must have at least one difficulty.");

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

            if (archiveReader.Filenames.Any(f => SupportedExtensions.VIDEO_EXTENSIONS.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
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
                throw new InvariantException($"The uploaded beatmap set's individual difficulties have inconsistent {valueName}. Please unify {valueName} before re-submitting.");

            return distinctValues.Single();
        }
    }

    public record BeatmapContent(string Filename, string MD5, Beatmap Beatmap)
    {
        public osu_beatmap GetDatabaseRow()
        {
            float beatLength = (float)Beatmap.GetMostCommonBeatLength();

            var result = new osu_beatmap
            {
                beatmap_id = (uint)Beatmap.BeatmapInfo.OnlineID,
                filename = Filename,
                checksum = MD5,
                version = Beatmap.BeatmapInfo.DifficultyName,
                diff_drain = Beatmap.Difficulty.DrainRate,
                diff_size = Beatmap.Difficulty.CircleSize,
                diff_overall = Beatmap.Difficulty.OverallDifficulty,
                diff_approach = Beatmap.Difficulty.ApproachRate,
                bpm = beatLength > 0 ? 60000 / beatLength : 0,
                total_length = (uint)(Beatmap.CalculatePlayableLength() / 1000),
                hit_length = (uint)(Beatmap.CalculateDrainLength() / 1000),
                playmode = (ushort)Beatmap.BeatmapInfo.Ruleset.OnlineID,
            };

            countObjectsByType(Beatmap, result);
            return result;
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
    }

    public record struct BeatmapPackageParseResult(
        osu_beatmapset BeatmapSet,
        PackageFile[] Files);
}
