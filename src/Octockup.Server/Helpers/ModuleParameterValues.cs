// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Helpers
{
    public static class ModuleParameterValues
    {
        public static bool IsSecret(string name) => name is "password" or "accessKey" or "secretKey";

        public static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> saved,
            IReadOnlyDictionary<string, string> changes)
        {
            Dictionary<string, string> merged = new(saved);
            foreach (KeyValuePair<string, string> change in changes)
            {
                if (IsSecret(change.Key) && string.IsNullOrWhiteSpace(change.Value))
                {
                    continue;
                }
                merged[change.Key] = change.Value;
            }
            return merged;
        }
    }
}
