// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text;
using Microsoft.AspNetCore.Http;
using Moq;
using osu.Framework.Extensions;
using osu.Game.IO.Archives;
using osu.Server.BeatmapSubmission.Services;

namespace osu.Server.BeatmapSubmission.Tests
{
    public class BeatmapPackagePatcherTest
    {
        private readonly Mock<IBeatmapStorage> storageMock;
        private readonly BeatmapPackagePatcher patcher;

        public BeatmapPackagePatcherTest()
        {
            storageMock = new Mock<IBeatmapStorage>();
            patcher = new BeatmapPackagePatcher(storageMock.Object);
        }

        [Fact]
        public async Task Patch_NoChanges()
        {
            storageMock.Setup(s => s.ExtractBeatmapSetAsync(It.IsAny<uint>(), It.IsAny<string>()))
                       .Callback((uint _, string basePath) =>
                       {
                           File.WriteAllText(Path.Combine(basePath, "first.txt"), "First file");
                           File.WriteAllText(Path.Combine(basePath, "second.txt"), "Second file");
                           File.WriteAllText(Path.Combine(basePath, "third.txt"), "Third file");
                       });

            var stream = await patcher.PatchBeatmapSetAsync(1234, [], []);

            using var reader = new ZipArchiveReader(stream);

            Assert.Equal(3, reader.Filenames.Count());

            Assert.Equal("First file", Encoding.UTF8.GetString(await reader.GetStream("first.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Second file", Encoding.UTF8.GetString(await reader.GetStream("second.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Third file", Encoding.UTF8.GetString(await reader.GetStream("third.txt").ReadAllBytesToArrayAsync()));
        }

        [Fact]
        public async Task Patch_AddFile()
        {
            storageMock.Setup(s => s.ExtractBeatmapSetAsync(It.IsAny<uint>(), It.IsAny<string>()))
                       .Callback((uint _, string basePath) =>
                       {
                           File.WriteAllText(Path.Combine(basePath, "first.txt"), "First file");
                           File.WriteAllText(Path.Combine(basePath, "second.txt"), "Second file");
                           File.WriteAllText(Path.Combine(basePath, "third.txt"), "Third file");
                       });

            var newFileStream = new MemoryStream("Fourth file"u8.ToArray());

            var stream = await patcher.PatchBeatmapSetAsync(1234,
                [new FormFile(newFileStream, 0, newFileStream.Length, "filesChanged", "fourth.txt")],
                []);

            using var reader = new ZipArchiveReader(stream);

            Assert.Equal(4, reader.Filenames.Count());

            Assert.Equal("First file", Encoding.UTF8.GetString(await reader.GetStream("first.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Second file", Encoding.UTF8.GetString(await reader.GetStream("second.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Third file", Encoding.UTF8.GetString(await reader.GetStream("third.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Fourth file", Encoding.UTF8.GetString(await reader.GetStream("fourth.txt").ReadAllBytesToArrayAsync()));
        }

        [Fact]
        public async Task Patch_ModifyFile()
        {
            storageMock.Setup(s => s.ExtractBeatmapSetAsync(It.IsAny<uint>(), It.IsAny<string>()))
                       .Callback((uint _, string basePath) =>
                       {
                           File.WriteAllText(Path.Combine(basePath, "first.txt"), "First file");
                           File.WriteAllText(Path.Combine(basePath, "second.txt"), "Second file");
                           File.WriteAllText(Path.Combine(basePath, "third.txt"), "Third file");
                       });

            var newFileStream = new MemoryStream("Third file but with changes"u8.ToArray());

            var stream = await patcher.PatchBeatmapSetAsync(1234,
                [new FormFile(newFileStream, 0, newFileStream.Length, "filesChanged", "third.txt")],
                []);

            using var reader = new ZipArchiveReader(stream);

            Assert.Equal(3, reader.Filenames.Count());

            Assert.Equal("First file", Encoding.UTF8.GetString(await reader.GetStream("first.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Second file", Encoding.UTF8.GetString(await reader.GetStream("second.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Third file but with changes", Encoding.UTF8.GetString(await reader.GetStream("third.txt").ReadAllBytesToArrayAsync()));
        }

        [Fact]
        public async Task Patch_DeleteFile()
        {
            storageMock.Setup(s => s.ExtractBeatmapSetAsync(It.IsAny<uint>(), It.IsAny<string>()))
                       .Callback((uint _, string basePath) =>
                       {
                           File.WriteAllText(Path.Combine(basePath, "first.txt"), "First file");
                           File.WriteAllText(Path.Combine(basePath, "second.txt"), "Second file");
                           File.WriteAllText(Path.Combine(basePath, "third.txt"), "Third file");
                       });

            var stream = await patcher.PatchBeatmapSetAsync(1234, [], ["first.txt", "third.txt"]);

            using var reader = new ZipArchiveReader(stream);

            Assert.Single(reader.Filenames);

            Assert.Null(reader.GetStream("first.txt"));
            Assert.Equal("Second file", Encoding.UTF8.GetString(await reader.GetStream("second.txt").ReadAllBytesToArrayAsync()));
            Assert.Null(reader.GetStream("third.txt"));
        }

        [Fact]
        public async Task Patch_SubdirectoryHandlingWithNoChanges()
        {
            storageMock.Setup(s => s.ExtractBeatmapSetAsync(It.IsAny<uint>(), It.IsAny<string>()))
                       .Callback((uint _, string basePath) =>
                       {
                           File.WriteAllText(Path.Combine(basePath, "first.txt"), "First file");
                           File.WriteAllText(Path.Combine(basePath, "second.txt"), "Second file");
                           File.WriteAllText(Path.Combine(basePath, "third.txt"), "Third file");

                           Directory.CreateDirectory(Path.Combine(basePath, "subdir"));
                           File.WriteAllText(Path.Combine(basePath, "subdir", "another.txt"), "Wedged in there");
                       });

            var stream = await patcher.PatchBeatmapSetAsync(1234, [], []);

            using var reader = new ZipArchiveReader(stream);

            Assert.Equal(4, reader.Filenames.Count());

            Assert.Equal("First file", Encoding.UTF8.GetString(await reader.GetStream("first.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Second file", Encoding.UTF8.GetString(await reader.GetStream("second.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Third file", Encoding.UTF8.GetString(await reader.GetStream("third.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Wedged in there", Encoding.UTF8.GetString(await reader.GetStream("subdir/another.txt").ReadAllBytesToArrayAsync()));
        }

        [Fact]
        public async Task Patch_CreateNewSubdirectory()
        {
            storageMock.Setup(s => s.ExtractBeatmapSetAsync(It.IsAny<uint>(), It.IsAny<string>()))
                       .Callback((uint _, string basePath) =>
                       {
                           File.WriteAllText(Path.Combine(basePath, "first.txt"), "First file");
                           File.WriteAllText(Path.Combine(basePath, "second.txt"), "Second file");
                           File.WriteAllText(Path.Combine(basePath, "third.txt"), "Third file");

                           Directory.CreateDirectory(Path.Combine(basePath, "subdir"));
                           File.WriteAllText(Path.Combine(basePath, "subdir", "another.txt"), "Wedged in there");
                       });

            var firstFileStream = new MemoryStream("Another subdir"u8.ToArray());
            var secondFileStream = new MemoryStream("Two levels deep"u8.ToArray());

            var stream = await patcher.PatchBeatmapSetAsync(1234,
                [
                    new FormFile(firstFileStream, 0, firstFileStream.Length, "filesChanged", "subdir2/more.txt"),
                    new FormFile(secondFileStream, 0, secondFileStream.Length, "filesChanged", "subdir/another/wow.txt"),
                ],
                []);

            using var reader = new ZipArchiveReader(stream);

            Assert.Equal(6, reader.Filenames.Count());

            Assert.Equal("First file", Encoding.UTF8.GetString(await reader.GetStream("first.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Second file", Encoding.UTF8.GetString(await reader.GetStream("second.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Third file", Encoding.UTF8.GetString(await reader.GetStream("third.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Wedged in there", Encoding.UTF8.GetString(await reader.GetStream("subdir/another.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Another subdir", Encoding.UTF8.GetString(await reader.GetStream("subdir2/more.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Two levels deep", Encoding.UTF8.GetString(await reader.GetStream("subdir/another/wow.txt").ReadAllBytesToArrayAsync()));
        }

        [Fact]
        public async Task Patch_DeleteSubdirectory()
        {
            storageMock.Setup(s => s.ExtractBeatmapSetAsync(It.IsAny<uint>(), It.IsAny<string>()))
                       .Callback((uint _, string basePath) =>
                       {
                           File.WriteAllText(Path.Combine(basePath, "first.txt"), "First file");
                           File.WriteAllText(Path.Combine(basePath, "second.txt"), "Second file");
                           File.WriteAllText(Path.Combine(basePath, "third.txt"), "Third file");

                           Directory.CreateDirectory(Path.Combine(basePath, "subdir"));
                           File.WriteAllText(Path.Combine(basePath, "subdir", "another.txt"), "Wedged in there");
                       });

            var stream = await patcher.PatchBeatmapSetAsync(1234, [], ["subdir/another.txt"]);

            using var reader = new ZipArchiveReader(stream);

            Assert.Equal(3, reader.Filenames.Count());

            Assert.Equal("First file", Encoding.UTF8.GetString(await reader.GetStream("first.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Second file", Encoding.UTF8.GetString(await reader.GetStream("second.txt").ReadAllBytesToArrayAsync()));
            Assert.Equal("Third file", Encoding.UTF8.GetString(await reader.GetStream("third.txt").ReadAllBytesToArrayAsync()));
            Assert.Null(reader.GetStream("subdir/another.txt"));
        }
    }
}
