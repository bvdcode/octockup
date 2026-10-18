// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace Octockup.Tests
{
    internal class DeletionBatchInterceptor : DbCommandInterceptor
    {
        public CancellationTokenSource? CancelAfterBatch { get; set; }
        public bool FailSnapshotDeletion { get; set; }
        public List<int> DeletedBatchSizes { get; } = [];
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            if (FailSnapshotDeletion && command.CommandText.StartsWith("DELETE FROM snapshots ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Snapshot deletion failure");
            }
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE FROM snapshot_files ", StringComparison.Ordinal))
            {
                DeletedBatchSizes.Add(result);
                if (CancelAfterBatch is not null)
                {
                    await CancelAfterBatch.CancelAsync();
                }
            }
            return result;
        }
    }
}
