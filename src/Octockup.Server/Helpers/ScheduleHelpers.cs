// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Models.Enums;

namespace Octockup.Server.Helpers
{
    public static class ScheduleHelpers
    {
        public static async Task<Schedule?> GetNextScheduleAsync(IQueryable<Schedule> _schedules, CancellationToken cancellationToken)
        {
            DateTime now = DateTime.UtcNow;

            List<Schedule> schedules = await _schedules
                .Where(x => x.Backup.DeletionRequestedAt == null &&
                    !x.Backup.Snapshots.Any(s => s.DeletionRequestedAt != null))
                .Include(x => x.Backup)
                .ThenInclude(b => b.Source)
                .Include(x => x.Backup)
                .ThenInclude(b => b.Storage)
                .ToListAsync(cancellationToken: cancellationToken);

            Schedule? best = null;
            DateTime? bestTime = null;

            foreach (Schedule? sch in schedules)
            {
                if (sch.Status == ScheduleStatus.Running)
                {
                    return sch;
                }

                DateTime? nextRun = CalculateNextRun(sch, now);
                if (nextRun == null)
                {
                    continue;
                }

                // Only consider schedules due to run now or earlier
                if (nextRun > now)
                {
                    continue;
                }

                if (bestTime == null || nextRun < bestTime)
                {
                    best = sch;
                    bestTime = nextRun;
                }
            }

            return best;
        }

        public static DateTime? CalculateNextRun(Schedule schedule, DateTime now)
        {
            DateTime utcNow = NormalizeUtc(now);
            DateTime startAtUtc = NormalizeUtc(schedule.StartAt);
            DateTime? finishedAtUtc = schedule.FinishedAt.HasValue ? NormalizeUtc(schedule.FinishedAt.Value) : null;

            if (schedule.Status == ScheduleStatus.Running)
            {
                // Currently running → interrupted, run now
                return utcNow;
            }

            // One-time job (Interval = null)
            if (schedule.Interval is null)
            {
                // Not started yet → next start
                if (finishedAtUtc is null)
                {
                    return startAtUtc;
                }

                // already executed → no more runs
                return null;
            }

            // Periodic job
            TimeSpan interval = schedule.Interval.Value;

            // If StartAt is in the future → not started yet
            if (startAtUtc > utcNow)
            {
                return startAtUtc;
            }

            // If never finished yet → first run = StartAt
            if (finishedAtUtc is null)
            {
                return startAtUtc;
            }

            // Next run strictly from last finish
            return finishedAtUtc.Value.Add(interval);
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
                _ => value
            };
        }

        public static string SplitPlainHash(string hash, char pathSeparator)
        {
            // format: aa/bb/ccddeeff...
            return $"{hash[..2]}{pathSeparator}{hash.Substring(2, 2)}{pathSeparator}{hash[4..]}.{CompressionHelpers.Extension}";
        }

        /// <summary>
        /// Checks if a directory should be ignored during enumeration.
        /// This allows skipping entire directory trees without iterating their contents.
        /// </summary>
        public static bool IsDirectoryIgnored(string relativePath, ICollection<string> ignoredPaths, char pathSeparator)
        {
            // Normalize path to use forward slashes for pattern matching
            string normalizedPath = "/" + relativePath.Replace(pathSeparator, '/').Trim('/');

            foreach (string pattern in ignoredPaths)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                string trimmedPattern = pattern.Trim().Replace('\\', '/').TrimEnd('/');

                if (trimmedPattern.StartsWith('/'))
                {
                    // Pattern starts with '/' → match from root
                    // Check if directory matches the pattern or is a subdirectory of it
                    if (MatchesDirectoryPattern(normalizedPath, trimmedPattern))
                    {
                        return true;
                    }
                }
                else
                {
                    // Pattern without leading '/' → match anywhere in path
                    // Check if the pattern appears as a segment or segment sequence in the path
                    if (ContainsPathPattern(normalizedPath, "/" + trimmedPattern))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if a path should be ignored based on ignore patterns.
        /// Patterns starting with '/' match from root.
        /// Patterns without leading '/' match anywhere in path.
        /// Supports simple wildcards: '*' matches any characters.
        /// </summary>
        public static bool IsPathIgnored(string path, string? fileName, ICollection<string> ignoredPaths)
        {
            // Normalize path to use forward slashes
            string normalizedPath = path.Replace('\\', '/');
            if (!normalizedPath.StartsWith('/'))
            {
                normalizedPath = "/" + normalizedPath;
            }

            foreach (string pattern in ignoredPaths)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                string trimmedPattern = pattern.Trim().Replace('\\', '/').TrimEnd('/');

                // Check if filename matches exactly (for patterns like "swap.img")
                if (fileName != null && !trimmedPattern.Contains('/') && MatchesPattern(fileName, trimmedPattern))
                {
                    return true;
                }

                // Pattern starts with '/' → match from root
                if (trimmedPattern.StartsWith('/'))
                {
                    if (MatchesPathPattern(normalizedPath, trimmedPattern))
                    {
                        return true;
                    }
                }
                else
                {
                    // Pattern without leading '/' → match anywhere in path
                    // This should match segment sequences like "postgres/data/18/docker/pg_wal"
                    if (ContainsPathPattern(normalizedPath, "/" + trimmedPattern))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if a path matches a pattern, where the pattern matches the path or any parent directory.
        /// </summary>
        private static bool MatchesPathPattern(string path, string pattern)
        {
            // Check exact match or path starts with pattern as a directory
            if (MatchesPattern(path, pattern))
            {
                return true;
            }

            // Check if path is inside a directory that matches the pattern
            if (!pattern.Contains('*'))
            {
                // For non-wildcard patterns, check if path starts with pattern + /
                return path.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase) ||
                       path.Equals(pattern, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// Checks if a directory path matches a pattern (for directory skipping).
        /// </summary>
        private static bool MatchesDirectoryPattern(string dirPath, string pattern)
        {
            // Directory matches if it equals the pattern, is inside the pattern, or the pattern is inside the directory
            if (!pattern.Contains('*'))
            {
                // Exact match
                if (dirPath.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                // Directory is inside ignored path
                if (dirPath.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                // Ignored path is inside this directory (we should skip because children will be ignored)
                // Actually no - we should NOT skip parent directories, only the exact ones
                return false;
            }

            return MatchesPattern(dirPath, pattern);
        }

        /// <summary>
        /// Checks if a path contains a pattern anywhere (for patterns without leading /).
        /// </summary>
        private static bool ContainsPathPattern(string path, string patternWithSlash)
        {
            if (!patternWithSlash.Contains('*'))
            {
                // Check if pattern appears anywhere as a path segment sequence
                // e.g., "/a/b/postgres/data/x" contains "/postgres/data"
                int idx = path.IndexOf(patternWithSlash, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // Found - now check it's at segment boundary
                    int endIdx = idx + patternWithSlash.Length;
                    // Must end at end of string or at a path separator
                    return endIdx >= path.Length || path[endIdx] == '/';
                }
                return false;
            }

            // For wildcard patterns, we need to check each possible starting position
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string[] patternSegments = patternWithSlash.Split('/', StringSplitOptions.RemoveEmptyEntries);

            for (int startIdx = 0; startIdx <= segments.Length - patternSegments.Length; startIdx++)
            {
                string subPath = "/" + string.Join("/", segments.Skip(startIdx));
                if (MatchesPattern(subPath, patternWithSlash))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Matches a path against a pattern with simple wildcard support.
        /// Supports '*' to match any characters.
        /// </summary>
        private static bool MatchesPattern(string path, string pattern)
        {
            // Simple wildcard support: convert pattern to regex-like matching
            if (!pattern.Contains('*'))
            {
                // No wildcards - simple prefix match
                return path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
            }

            // Split pattern by '*' and check each part exists in order
            string[] parts = pattern.Split('*', StringSplitOptions.None);
            int currentIndex = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                int foundIndex = path.IndexOf(part, currentIndex, StringComparison.OrdinalIgnoreCase);
                if (foundIndex == -1)
                {
                    return false;
                }

                // First part must match at the start (unless pattern starts with *)
                if (i == 0 && !pattern.StartsWith('*') && foundIndex != currentIndex)
                {
                    return false;
                }

                currentIndex = foundIndex + part.Length;
            }

            // Last part must match at the end (unless pattern ends with *)
            if (parts.Length > 0 && !pattern.EndsWith('*'))
            {
                string lastPart = parts[^1];
                if (!string.IsNullOrEmpty(lastPart) && !path.EndsWith(lastPart, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
