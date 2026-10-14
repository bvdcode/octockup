// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace Octockup.Tests
{
    internal class DownloadMetadataQueryInterceptor : DbCommandInterceptor
    {
        public int QueryCount { get; private set; }
        public int MaximumBatchSize { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM uploaded_hashes", StringComparison.OrdinalIgnoreCase))
            {
                QueryCount++;
                foreach (DbParameter parameter in command.Parameters)
                {
                    if (parameter.Value is string[] batch)
                    {
                        MaximumBatchSize = Math.Max(MaximumBatchSize, batch.Length);
                    }
                }
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
