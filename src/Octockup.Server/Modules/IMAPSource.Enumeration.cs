// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using MailKit;
using MailKit.Net.Imap;
using Octockup.Server.Helpers;
using Octockup.Server.Models;

namespace Octockup.Server.Modules
{
    public partial class IMAPSource
    {
        private IMailFolder GetInboxFolder(CancellationToken cancellationToken)
        {
            _imapLock.Wait(cancellationToken);
            try
            {
                ImapClient client = _client
                    ?? throw new InvalidOperationException("IMAP client is not connected.");
                return client.Inbox
                    ?? throw new InvalidOperationException("IMAP server did not provide an Inbox folder.");
            }
            finally
            {
                _imapLock.Release();
            }
        }

        private IEnumerable<BackupFileInfo> EnumerateUniqueFolderFiles(
            IEnumerable<IMailFolder> folders,
            ISet<string> visitedFolders,
            string? excludedFolderName,
            CancellationToken cancellationToken)
        {
            foreach (IMailFolder folder in folders)
            {
                if (!visitedFolders.Add(folder.FullName)
                    || string.Equals(
                        folder.FullName,
                        excludedFolderName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (BackupFileInfo file in EnumerateFolderFiles(folder, cancellationToken))
                {
                    yield return file;
                }
            }
        }

        private IEnumerable<BackupFileInfo> EnumerateFolderFiles(
            IMailFolder folder,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkipFolder(folder))
            {
                yield break;
            }

            using IEnumerator<BackupFileInfo> enumerator = ProcessFolderFiles(
                folder,
                cancellationToken).GetEnumerator();
            while (TryMoveNext(enumerator, folder))
            {
                yield return enumerator.Current;
            }
        }

        private bool ShouldSkipFolder(IMailFolder folder)
        {
            string folderPath = "/" + folder.FullName;
            if (_ignoredPaths is not null
                && ScheduleHelpers.IsPathIgnored(folderPath, folder.Name, _ignoredPaths))
            {
                _logger.LogDebug(
                    "Skipping ignored IMAP folder during file enumeration: {Folder}",
                    folderPath);
                return true;
            }

            if (string.IsNullOrWhiteSpace(folder.FullName))
            {
                _logger.LogDebug("Skipping folder with empty name");
                return true;
            }

            if ((folder.Attributes & FolderAttributes.NoSelect) == 0)
            {
                return false;
            }

            _logger.LogDebug(
                "Skipping NoSelect folder (namespace container): {Folder}",
                folder.FullName);
            return true;
        }

        private IEnumerable<BackupFileInfo> ProcessFolderFiles(
            IMailFolder folder,
            CancellationToken cancellationToken)
        {
            int? total = null;
            for (int start = 0; !total.HasValue || start < total.Value; start += _batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMailFolder? openedFolder = null;
                IReadOnlyList<IMessageSummary>? summaries;
                _imapLock.Wait(cancellationToken);
                try
                {
                    openedFolder = OpenFolder(folder, cancellationToken);
                    if (openedFolder is null)
                    {
                        yield break;
                    }
                    if (!total.HasValue)
                    {
                        total = openedFolder.Count;
                        _logger.LogInformation(
                            "Enumerating {Total} emails in folder {Folder} in batches of {Batch}",
                            total.Value,
                            openedFolder.FullName,
                            _batchSize);
                    }
                    if (start >= total.Value)
                    {
                        yield break;
                    }
                    int end = Math.Min(start + _batchSize - 1, total.Value - 1);
                    summaries = FetchSummaries(
                        openedFolder,
                        start,
                        end,
                        cancellationToken);
                }
                finally
                {
                    CloseFolder(openedFolder, cancellationToken);
                    _imapLock.Release();
                }
                if (summaries is null)
                {
                    yield break;
                }
                foreach (IMessageSummary summary in summaries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BackupFileInfo? file = CreateFileInfo(folder, summary);
                    if (file is not null)
                    {
                        yield return file;
                    }
                }
            }
        }

        private IMailFolder? OpenFolder(IMailFolder folder, CancellationToken cancellationToken)
        {
            ImapClient client = _client
                ?? throw new InvalidOperationException("IMAP client is not connected.");
            IMailFolder? openedFolder = string.IsNullOrEmpty(folder.FullName)
                ? client.Inbox
                : client.GetFolder(folder.FullName, cancellationToken);

            if (openedFolder is null)
            {
                _logger.LogDebug("Skipping unavailable IMAP folder: {Folder}", folder.FullName);
                return null;
            }

            if ((openedFolder.Attributes & FolderAttributes.NoSelect) != 0)
            {
                _logger.LogDebug(
                    "Skipping NoSelect folder after fresh reference (namespace container): {Folder}",
                    openedFolder.FullName);
                return null;
            }

            if (!openedFolder.IsOpen)
            {
                openedFolder.Open(FolderAccess.ReadOnly, cancellationToken);
            }

            return openedFolder;
        }

        private IReadOnlyList<IMessageSummary>? FetchSummaries(
            IMailFolder folder,
            int start,
            int end,
            CancellationToken cancellationToken)
        {
            try
            {
                IList<IMessageSummary> fetched = folder.Fetch(
                    start,
                    end,
                    MessageSummaryItems.UniqueId
                        | MessageSummaryItems.InternalDate
                        | MessageSummaryItems.Size,
                    cancellationToken: cancellationToken);
                return fetched is IReadOnlyList<IMessageSummary> summaries
                    ? summaries
                    : [.. fetched];
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to fetch summaries for {Folder} range {Start}-{End}",
                    folder.FullName,
                    start,
                    end);
                return null;
            }
        }

        private BackupFileInfo? CreateFileInfo(IMailFolder folder, IMessageSummary summary)
        {
            UniqueId uniqueId = summary.UniqueId;
            if (!uniqueId.IsValid)
            {
                return null;
            }

            string fileName = $"{uniqueId.Id}.eml";
            string normalizedFolderPath = NormalizeFolderPath(folder.FullName);
            string filePath = string.IsNullOrEmpty(normalizedFolderPath)
                ? fileName
                : $"{normalizedFolderPath}/{fileName}";
            if (_ignoredPaths is not null
                && ScheduleHelpers.IsPathIgnored("/" + filePath, fileName, _ignoredPaths))
            {
                _logger.LogDebug("Skipping ignored email: {File}", filePath);
                return null;
            }

            return new BackupFileInfo
            {
                Path = filePath,
                Name = fileName,
                Size = summary.Size,
                LastModified = summary.InternalDate?.UtcDateTime,
            };
        }

        private string NormalizeFolderPath(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)
                || !_serverDirectorySeparator.HasValue
                || _serverDirectorySeparator.Value == '/')
            {
                return folderPath;
            }

            return folderPath.Replace(_serverDirectorySeparator.Value, '/');
        }

        private void CloseFolder(IMailFolder? folder, CancellationToken cancellationToken)
        {
            if (folder is null || !folder.IsOpen)
            {
                return;
            }

            try
            {
                folder.Close(cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to close IMAP folder {Folder}", folder.FullName);
            }
        }

        private bool TryMoveNext(IEnumerator<BackupFileInfo> enumerator, IMailFolder folder)
        {
            try
            {
                return enumerator.MoveNext();
            }
            catch (ImapCommandException exception) when (exception.Message.Contains("Unknown Mailbox"))
            {
                _logger.LogWarning(
                    "Folder {Folder} does not exist or is not accessible, skipping",
                    folder.FullName);
                return false;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error enumerating folder {Folder}", folder.FullName);
                return false;
            }
        }
    }
}
