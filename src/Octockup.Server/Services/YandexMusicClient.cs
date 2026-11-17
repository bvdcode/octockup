// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Octockup.Server.Models.YandexMusic;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Octockup.Server.Services
{
    public class YandexMusicClient(HttpClient http, YandexMusicOptions options, ILogger logger)
    {
        private const string ApiBase = "https://api.music.yandex.net/";
        private const string DesktopClient = "YandexMusicDesktopAppWindows/5.95.0";
        private const string DesktopSigningKey = "kzqU4XhfCaY6B6JTHODeq5";
        private const string LyricsSigningKey = "p93jhgh689SBReK6ghtw62";
        private const string Codecs = "flac,flac-mp4,aac-mp4,he-aac-mp4,aac,he-aac,mp3";
        private string _token = string.Empty;

        public void SetToken(string token) => _token = token;

        public async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken,
            bool desktop = false, IReadOnlyDictionary<string, string>? form = null)
        {
            using HttpResponseMessage response = await SendAsync(new Uri(ApiBase + path), true, cancellationToken, desktop, form);
            using CancellationTokenSource timeout = CreateTimeout(cancellationToken);
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return document.RootElement.GetProperty("result").Clone();
        }

        public async Task<YandexMusicDownload> GetDownloadAsync(string trackId, CancellationToken cancellationToken)
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            string sign = Sign($"{timestamp}{trackId}lossless{Codecs.Replace(",", string.Empty)}raw", DesktopSigningKey).TrimEnd('=');
            string query = $"get-file-info?ts={timestamp}&trackId={Uri.EscapeDataString(trackId)}&quality=lossless"
                + $"&codecs={Codecs}&transports=raw&sign={Uri.EscapeDataString(sign)}";
            JsonElement result = await GetAsync(query, cancellationToken, desktop: true);
            JsonElement info = result.GetProperty("downloadInfo");
            if (info.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(key.GetString()))
            {
                throw new InvalidDataException("Yandex Music returned encrypted audio for a raw download request.");
            }
            string extension = info.GetProperty("codec").GetString() switch
            {
                "flac" => ".flac",
                "mp3" => ".mp3",
                "flac-mp4" or "aac-mp4" or "he-aac-mp4" or "aac" or "he-aac" => ".m4a",
                _ => throw new InvalidDataException("Yandex Music returned an unsupported audio codec."),
            };
            Uri url = new(info.GetProperty("urls")[0].GetString()!);
            return new YandexMusicDownload(url, extension);
        }

        public async Task<byte[]> GetLyricsAsync(string trackId, string format, CancellationToken cancellationToken)
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            string sign = Uri.EscapeDataString(Sign(trackId + timestamp, LyricsSigningKey));
            JsonElement lyrics = await GetAsync(
                $"tracks/{Uri.EscapeDataString(trackId)}/lyrics?format={format}&timeStamp={timestamp}&sign={sign}", cancellationToken);
            Uri url = new(lyrics.GetProperty("downloadUrl").GetString()!);
            return await GetBytesAsync(url, cancellationToken);
        }

        public async Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(url, false, cancellationToken);
            using CancellationTokenSource timeout = CreateTimeout(cancellationToken);
            return await response.Content.ReadAsByteArrayAsync(timeout.Token);
        }

        public async Task DownloadAsync(Uri url, string path, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadOnceAsync(url, path, cancellationToken);
                    return;
                }
                catch (Exception ex) when (attempt < options.Attempts && !cancellationToken.IsCancellationRequested && IsTransient(ex))
                {
                    File.Delete(path);
                    logger.LogWarning(ex, "Yandex Music audio transfer failed; retrying attempt {Attempt}.", attempt + 1);
                    await Task.Delay(options.RetryDelay, cancellationToken);
                }
            }
        }

        private async Task DownloadOnceAsync(Uri url, string path, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(url, false, cancellationToken, retry: false);
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.Asynchronous);
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                using CancellationTokenSource timeout = CreateTimeout(cancellationToken);
                int read = await input.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        private async Task<HttpResponseMessage> SendAsync(Uri url, bool authenticated, CancellationToken cancellationToken,
            bool desktop = false, IReadOnlyDictionary<string, string>? form = null, bool retry = true)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
            {
                throw new InvalidDataException("Yandex Music returned an invalid media URL.");
            }
            for (int attempt = 1; ; attempt++)
            {
                using HttpRequestMessage request = new(form is null ? HttpMethod.Get : HttpMethod.Post, url);
                if (form is not null)
                {
                    request.Content = new FormUrlEncodedContent(form);
                }
                if (authenticated)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", _token);
                    request.Headers.Add("X-Yandex-Music-Client", desktop ? DesktopClient : "YandexMusicAndroid/24023621");
                }
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);
                try
                {
                    HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }
                    HttpStatusCode status = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"Yandex Music request failed (HTTP {(int)status}).", null, status);
                }
                catch (Exception ex) when (retry && attempt < options.Attempts && !cancellationToken.IsCancellationRequested && IsTransient(ex))
                {
                    logger.LogWarning(ex, "Yandex Music request failed; retrying attempt {Attempt}.", attempt + 1);
                    await Task.Delay(options.RetryDelay, cancellationToken);
                }
            }
        }

        private static bool IsTransient(Exception exception)
        {
            return exception switch
            {
                OperationCanceledException => true,
                HttpRequestException { StatusCode: null } => true,
                HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => true,
                HttpRequestException { StatusCode: not null } httpError => (int)httpError.StatusCode.Value >= 500,
                IOException => true,
                _ => false,
            };
        }

        private static string Sign(string message, string key)
        {
            return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message)));
        }

        private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
        {
            CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            return timeout;
        }
    }
}
