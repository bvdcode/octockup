// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Modules;
using Octockup.Server.Models.Dto;
using Octockup.Server.Modules;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;

namespace Octockup.Tests
{
    [Category("Integration")]
    public class ModuleParametersTests
    {
        private PostgresTestDatabase _database = null!;

        [OneTimeSetUp]
        public async Task CreateDatabaseAsync() => _database = await PostgresTestDatabase.CreateAsync();

        [OneTimeTearDown]
        public async Task DropDatabaseAsync() => await _database.DisposeAsync();

        [TestCase(false)]
        [TestCase(true)]
        public async Task Get_ReturnsSettingsWithoutStoredSecrets(bool target)
        {
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_database.ConnectionString);
            Module module = await GetModuleAsync(scenario, target);
            foreach (string key in new[] { "password", "accessKey", "secretKey" })
            {
                module.Params(scenario.Cipher)[key] = $"saved-{key}";
            }
            await scenario.Database.SaveChangesAsync();
            await using WebApplication app = await CreateServerAsync(scenario, module.UserId);
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(Url(module.Id));
            ModuleParametersDto? result = await response.Content.ReadFromJsonAsync<ModuleParametersDto>();

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(result!.StoredSecrets, Is.EquivalentTo(new[] { "password", "accessKey", "secretKey" }));
            Assert.That(result.Parameters.Keys, Does.Not.Contain("password").And.Not.Contain("accessKey").And.Not.Contain("secretKey"));
            Assert.That(result.Parameters["path"], Is.EqualTo(module.Params(scenario.Cipher)["path"]));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Update_ChangesOneCredentialAndPreservesExistingBackup(bool target)
        {
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_database.ConnectionString);
            await scenario.WriteSourceFileAsync("document.txt", [1, 2, 3]);
            Schedule schedule = await scenario.RunBackupAsync();
            Snapshot snapshot = await scenario.GetLatestSnapshotAsync();
            Module module = await GetModuleAsync(scenario, target);
            string path = module.Params(scenario.Cipher)["path"];
            module.Params(scenario.Cipher)["password"] = "old-password";
            module.Params(scenario.Cipher)["host"] = "storage.example.com";
            await scenario.Database.SaveChangesAsync();
            await using WebApplication app = await CreateServerAsync(scenario, module.UserId);
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.PatchAsJsonAsync(Url(module.Id), new { password = "new-password" });
            scenario.Database.ChangeTracker.Clear();
            Module updated = await scenario.Database.Modules.SingleAsync(x => x.Id == module.Id);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(updated.Params(scenario.Cipher)["password"], Is.EqualTo("new-password"));
            Assert.That(updated.Params(scenario.Cipher)["path"], Is.EqualTo(path));
            Assert.That(updated.Params(scenario.Cipher)["host"], Is.EqualTo("storage.example.com"));
            Assert.That(updated.EncryptedParameters, Does.Not.Contain("new-password"));
            Backup backup = await scenario.Database.Backups.SingleAsync(x => x.Id == scenario.BackupId);
            Assert.That(target ? backup.StorageId : backup.SourceId, Is.EqualTo(module.Id));
            Assert.That(await scenario.Database.Schedules.AnyAsync(x => x.Id == schedule.Id && x.BackupId == backup.Id), Is.True);
            Assert.That(await scenario.Database.Snapshots.AnyAsync(x => x.Id == snapshot.Id && x.BackupId == backup.Id), Is.True);
            Schedule next = await scenario.RunBackupAsync();
            Assert.That(next.Status, Is.EqualTo(Server.Models.Enums.ScheduleStatus.Completed), next.ErrorMessage);
        }

        [Test]
        public async Task Update_EmptySecretsKeepStoredValuesWhileAnEmptyPathIsSaved()
        {
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_database.ConnectionString);
            Module module = await GetModuleAsync(scenario, true);
            module.Params(scenario.Cipher)["secretKey"] = "keep-secret";
            await scenario.Database.SaveChangesAsync();
            await using WebApplication app = await CreateServerAsync(scenario, module.UserId);
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.PatchAsJsonAsync(Url(module.Id), new { secretKey = "", path = "", host = "new.example.com" });
            scenario.Database.ChangeTracker.Clear();
            Module updated = await scenario.Database.Modules.SingleAsync(x => x.Id == module.Id);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(updated.Params(scenario.Cipher)["secretKey"], Is.EqualTo("keep-secret"));
            Assert.That(updated.Params(scenario.Cipher)["path"], Is.Empty);
            Assert.That(updated.Params(scenario.Cipher)["host"], Is.EqualTo("new.example.com"));
        }

        [TestCase("GET")]
        [TestCase("PATCH")]
        [TestCase("POST")]
        public async Task Endpoints_RejectAnotherUsersModule(string method)
        {
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_database.ConnectionString);
            Module module = await GetModuleAsync(scenario, true);
            string encrypted = module.EncryptedParameters;
            await using WebApplication app = await CreateServerAsync(scenario, Guid.NewGuid());
            using HttpClient client = app.GetTestClient();
            using HttpRequestMessage request = new(new HttpMethod(method), Url(module.Id) + (method == "POST" ? "/test" : ""));
            if (method != "GET")
            {
                request.Content = JsonContent.Create(new { password = "replacement" });
            }

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            scenario.Database.ChangeTracker.Clear();
            Assert.That((await scenario.Database.Modules.SingleAsync(x => x.Id == module.Id)).EncryptedParameters, Is.EqualTo(encrypted));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Test_UsesStoredSettingsWithoutSavingDraftChanges(bool target)
        {
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_database.ConnectionString);
            await scenario.WriteSourceFileAsync("document.txt", [1, 2, 3]);
            Module module = await GetModuleAsync(scenario, target);
            string encrypted = module.EncryptedParameters;
            await using WebApplication app = await CreateServerAsync(scenario, module.UserId);
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.PostAsJsonAsync(Url(module.Id) + "/test", new { host = "draft.example.com" });

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
            scenario.Database.ChangeTracker.Clear();
            Assert.That((await scenario.Database.Modules.SingleAsync(x => x.Id == module.Id)).EncryptedParameters, Is.EqualTo(encrypted));
        }

        private static string Url(Guid moduleId) => $"/api/v1/modules/{moduleId}/parameters";

        private static async Task<Module> GetModuleAsync(BackupIntegrationScenario scenario, bool target)
        {
            Backup backup = await scenario.Database.Backups.SingleAsync(x => x.Id == scenario.BackupId);
            Guid id = target ? backup.StorageId : backup.SourceId;
            return await scenario.Database.Modules.SingleAsync(x => x.Id == id);
        }

        private static async Task<WebApplication> CreateServerAsync(BackupIntegrationScenario scenario, Guid userId)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Octockup.Server" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddControllers();
            builder.Services.AddAuthorization();
            builder.Services.AddAuthentication(TestAuthenticationHandler.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.AuthenticationScheme, _ => { });
            builder.Services.AddSingleton(scenario.Database);
            builder.Services.AddSingleton(scenario.Cipher);
            builder.Services.AddSingleton<IBackupProvider>(new FileSystemBackupSource(NullLogger<FileSystemBackupSource>.Instance));
            builder.Services.AddMediator(config => config.RegisterServicesFromAssemblyContaining<GetModuleParametersQuery>());
            WebApplication app = builder.Build();
            app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                context.User = new(new ClaimsIdentity([new Claim("sub", userId.ToString())], "Test"));
                await next(context);
            });
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            return app;
        }
    }
}
