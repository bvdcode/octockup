// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.AspNetCore.Extensions;
using Microsoft.AspNetCore.Mvc;
using Octockup.Server.Abstractions;
using Octockup.Server.Models;

namespace Octockup.Server.Helpers
{
    public static class TestHelpers
    {
        public static async Task<IActionResult> TestStorageAsync(ControllerBase controller, IBackupStorage storage, ILogger logger)
        {
            try
            {
                const string testFileName = "path_test_connection.txt";
                await storage.UploadAsync(testFileName, Stream.Null);
                IEnumerable<BackupFileInfo> result = storage.GetFiles(recursive: false);
                if (!result.Any(x => x.Name == testFileName))
                {
                    logger.LogWarning("Test file was not found in the storage after upload.");
                    return controller.ApiBadRequest("Test file was not found after upload.");
                }
                await storage.DeleteAsync(testFileName);
                return controller.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during storage connection test.");
                return controller.ApiBadRequest("Failed to connect to backup storage with provided parameters: " + ex.Message);
            }
        }

        public static async Task<IActionResult> TestSourceAsync(ControllerBase moduleController, IBackupSource source, ILogger logger)
        {
            try
            {
                _ = source.GetDirectories(recursive: false).Take(1).ToList();

                const int maxEnumeratedFiles = 100;
                const int maxTestedFiles = 10;

                List<BackupFileInfo> candidates = source
                    .GetFiles(recursive: true)
                    .Take(maxEnumeratedFiles)
                    .ToList();

                if (candidates.Count == 0)
                {
                    logger.LogWarning("No files found in the backup source during connection test.");
                    return moduleController.ApiBadRequest("No files found in the backup source to test file stream retrieval.");
                }

                int tested = 0;
                List<string> errors = new List<string>();

                foreach (BackupFileInfo? file in candidates)
                {
                    if (tested >= maxTestedFiles)
                    {
                        break;
                    }

                    Stream? testStream = null;
                    try
                    {
                        testStream = await source.GetFileStreamAsync(file);

                        if (testStream == null || testStream == Stream.Null)
                        {
                            logger.LogWarning("File '{FilePath}' returned null or empty stream during connection test.", file.Path);
                            errors.Add($"File '{file.Path}': returned null or empty stream");
                            tested++;
                            continue;
                        }

                        byte[] buffer = new byte[1024];
                        int read = 0;

                        if (testStream.CanRead)
                        {
                            read = await testStream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                        }
                        else
                        {
                            logger.LogWarning("File '{FilePath}' stream is not readable during connection test.", file.Path);
                            errors.Add($"File '{file.Path}': stream is not readable");
                            tested++;
                            continue;
                        }

                        if (read >= 0)
                        {
                            return moduleController.Ok(candidates);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error reading file '{FilePath}' during connection test.", file.Path);
                        errors.Add($"File '{file.Path}': {ex.Message}");
                        tested++;
                        continue;
                    }
                    finally
                    {
                        if (testStream is not null)
                        {
                            await testStream.DisposeAsync();
                        }
                    }

                    tested++;
                }

                string errorMessage = errors.Count > 0
                    ? string.Join("; ", errors.Take(3))
                    : "no files could be read";

                return moduleController.ApiBadRequest($"Failed to retrieve a readable stream from the backup source in the first {maxTestedFiles} files: {errorMessage}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during backup source connection test.");
                return moduleController.ApiBadRequest("Failed to connect to backup source with provided parameters: " + ex.Message);
            }
        }
    }
}
