// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel.DataAnnotations;
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
    public class BeatmapPackageParser
    {
        public static readonly HashSet<string> VALID_EXTENSIONS = new HashSet<string>([..SupportedExtensions.ALL_EXTENSIONS, @".osu", @".osb"], StringComparer.OrdinalIgnoreCase);

        private readonly string expectedCreator;

        public BeatmapPackageParser(string expectedCreator)
        {
            this.expectedCreator = expectedCreator;
        }

        public BeatmapPackageParseResult Parse(uint beatmapSetId, ArchiveReader archiveReader)
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

                    // slightly unfortunate but possible if the set ID is completely missing.
                    // refer to: https://github.com/ppy/osu/blob/a556049c1b29808b5dd9a3d65c7efb5d02315a0c/osu.Game/Beatmaps/Beatmap.cs#L45-L58,
                    // https://github.com/ppy/osu/blob/a556049c1b29808b5dd9a3d65c7efb5d02315a0c/osu.Game/Beatmaps/Formats/LegacyBeatmapDecoder.cs#L377-L379
                    if (beatmapContent.Beatmap.BeatmapInfo.BeatmapSet == null)
                        throw new InvariantException($"Beatmap has no beatmap set ID inside ({filename})");

                    if (beatmapContent.Beatmap.BeatmapInfo.BeatmapSet.OnlineID != beatmapSetId)
                        throw new InvariantException($"Beatmap has invalid beatmap set ID inside ({filename})");
                }

                var file = new PackageFile(
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
                );

                var errors = new List<ValidationResult>();
                if (!Validator.TryValidateObject(file.VersionFile, new ValidationContext(file.VersionFile), errors, validateAllProperties: true))
                    throw new InvariantException(string.Join(Environment.NewLine, errors.Select(r => r.ErrorMessage)));

                files.Add(file);
            }

            var beatmapSetRow = constructDatabaseRowForBeatmapset(beatmapSetId, archiveReader,
                files.Select(f => f.BeatmapContent).Where(c => c != null).ToArray()!);

            return new BeatmapPackageParseResult(beatmapSetRow, files.ToArray());
        }

        private static BeatmapContent getBeatmapContent(string filePath, Stream contents)
        {
            string fileName = Path.GetFileName(filePath);
            using var reader = new LineBufferedReader(contents, leaveOpen: true);
            var decoder = Decoder.GetDecoder<Beatmap>(reader);
            var beatmap = decoder.Decode(reader);

            if (beatmap.BeatmapVersion < LegacyBeatmapDecoder.LATEST_VERSION)
                throw new InvariantException($"Version of file \"{fileName}\" is too old (should be v14 or higher)");

            return new BeatmapContent(fileName, contents.ComputeMD5Hash(), beatmap);
        }

        private osu_beatmapset constructDatabaseRowForBeatmapset(uint beatmapSetId, ArchiveReader archiveReader, ICollection<BeatmapContent> beatmaps)
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
                filename = PackageFilenameFor(beatmapSetId),
            };

            string creator = getSingleValueFrom(beatmaps, c => c.Beatmap.Metadata.Author.Username, "Creator");
            if (creator != expectedCreator)
                throw new InvariantException("At least one difficulty has a specified creator that isn't the beatmap host's username.");

            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(result, new ValidationContext(result), errors, validateAllProperties: true))
                throw new InvariantException(string.Join(Environment.NewLine, errors.Select(r => r.ErrorMessage)));

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

        public static string PackageFilenameFor(uint beatmapSetId) => FormattableString.Invariant($"{beatmapSetId}.osz");

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
            var (_, endTime) = Beatmap.CalculatePlayableBounds();

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
                total_length = (uint)Math.Ceiling(endTime / 1000),
                hit_length = (uint)(Beatmap.CalculateDrainLength() / 1000),
                playmode = (ushort)Beatmap.BeatmapInfo.Ruleset.OnlineID,
            };

            countObjectsByType(Beatmap, result);

            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(result, new ValidationContext(result), errors, validateAllProperties: true))
                throw new InvariantException(string.Join(Environment.NewLine, errors.Select(r => r.ErrorMessage)));

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
