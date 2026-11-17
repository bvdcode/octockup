// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Octockup.Server.Collections;
using Octockup.Server.Models;
using Octockup.Server.Modules;

namespace Octockup.Tests
{
    public class ImapEnumerationTests
    {
        [Test]
        public async Task GetFiles_WhenListingIsPaused_AllowsDownloadingAnEmail()
        {
            await using ImapTestServer server = new(6);
            using IMAPSource source = server.CreateSource(batchSize: 2);
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
            using IEnumerator<BackupFileInfo> files = source.GetFiles(false, cancellation.Token).GetEnumerator();

            Assert.That(files.MoveNext(), Is.True);
            string email = await ReadAsync(source, files.Current, cancellation.Token);

            Assert.Multiple(() =>
            {
                Assert.That(email, Does.Contain("Body of INBOX message 1."));
                Assert.That(server.BodyRequests, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task GetFiles_WhenDownloadSwitchesMailbox_ReselectsTheListingMailbox()
        {
            await using ImapTestServer server = new(6);
            using IMAPSource source = server.CreateSource(batchSize: 2);
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
            using IEnumerator<BackupFileInfo> files = source.GetFiles(false, cancellation.Token).GetEnumerator();
            Assert.That(files.MoveNext(), Is.True);

            string email = await ReadAsync(source, new BackupFileInfo { Path = "Archive/1.eml" }, cancellation.Token);
            List<string> paths = [files.Current.Path];
            while (files.MoveNext())
            {
                paths.Add(files.Current.Path);
            }

            Assert.Multiple(() =>
            {
                Assert.That(email, Does.Contain("Body of Archive message 1."));
                Assert.That(paths, Is.EqualTo(Enumerable.Range(1, 6).Select(x => $"INBOX/{x}.eml")));
                Assert.That(server.SummaryFolders, Is.EqualTo(new[] { "INBOX", "INBOX", "INBOX" }));
            });
        }

        [Test]
        public async Task GetFiles_WhenQueueIsFull_DownloadsBeforeListingCompletes()
        {
            const int batchSize = 4_096;
            const int queueCapacity = batchSize * 2;
            const int messageCount = 20_000;
            await using ImapTestServer server = new(messageCount);
            using IMAPSource source = server.CreateSource();
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
            TaskCompletionSource listingFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using LazyLoader<BackupFileInfo> loader = new(
                TrackCompletion(source.GetFiles(false, cancellation.Token), listingFinished), queueCapacity);
            using IEnumerator<BackupFileInfo[]> batches = loader
                .GetBatches(batchSize, TimeSpan.FromSeconds(1), cancellation.Token).GetEnumerator();
            try
            {
                Assert.That(batches.MoveNext(), Is.True);
                Assert.That(SpinWait.SpinUntil(() => loader.Total == batchSize + queueCapacity,
                    TimeSpan.FromSeconds(3)), Is.True);
                Assert.That(loader.IsEnumerationCompleted, Is.False);
                int listed = batches.Current.Length;
                string firstEmail = await ReadAsync(source, batches.Current[0], cancellation.Token);
                while (batches.MoveNext())
                {
                    listed += batches.Current.Length;
                    await ReadAsync(source, batches.Current[0], cancellation.Token);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(firstEmail, Does.Contain("Body of INBOX message 1."));
                    Assert.That(listed, Is.EqualTo(messageCount));
                    Assert.That(server.BodyRequests, Is.EqualTo(5));
                    Assert.That(loader.IsEnumerationCompleted, Is.True);
                });
            }
            finally
            {
                await cancellation.CancelAsync();
                loader.Dispose();
                await listingFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }

        private static IEnumerable<BackupFileInfo> TrackCompletion(
            IEnumerable<BackupFileInfo> files, TaskCompletionSource completion)
        {
            try
            {
                foreach (BackupFileInfo file in files)
                {
                    yield return file;
                }
            }
            finally
            {
                completion.SetResult();
            }
        }

        private static async Task<string> ReadAsync(IMAPSource source, BackupFileInfo file, CancellationToken cancellationToken)
        {
            await using Stream stream = await source.GetFileStreamAsync(file, cancellationToken);
            using StreamReader reader = new(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
    }
}
