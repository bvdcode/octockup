// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace Octockup.Tests
{
    internal class StorageCleanupFailureInterceptor : DbCommandInterceptor
    {
        public int ReferenceQueryCount { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("unnest(s.chunk_hashes)", StringComparison.Ordinal))
            {
                ReferenceQueryCount++;
                throw new TimeoutException("Cleanup reference query timed out.");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
