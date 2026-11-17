// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Octockup.Server.Abstractions;
using Octockup.Server.Models.YandexMusic;
using Octockup.Server.Modules;

namespace Octockup.Server.Extensions
{
    public static class YandexMusicExtensions
    {
        public static IServiceCollection AddYandexMusic(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<YandexMusicOptions>().Bind(configuration.GetSection("YandexMusic"))
                .Validate(x => x.Timeout > TimeSpan.Zero && x.TrackDelay >= TimeSpan.Zero
                    && x.RetryDelay >= TimeSpan.Zero && x.Attempts > 0 && !string.IsNullOrWhiteSpace(x.CacheDirectory))
                .ValidateOnStart();
            services.AddHttpClient(nameof(YandexMusicSource));
            services.AddScoped<IBackupProvider, YandexMusicSource>();
            return services;
        }
    }
}
