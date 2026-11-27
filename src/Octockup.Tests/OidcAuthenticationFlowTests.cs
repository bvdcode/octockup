// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Crypto;
using EasyExtensions.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Authentication;
using Octockup.Server.Models.Dto;
using Octockup.Server.Models.Requests;
using Octockup.Server.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Octockup.Tests
{
    [Category("Integration")]
    public partial class OidcAuthenticationFlowTests
    {
        private PostgresTestDatabase _database = null!;
        private AesGcmStreamCipher _cipher = null!;
        private RSA _rsa = null!;

        [OneTimeSetUp]
        public async Task CreateDatabaseAsync()
        {
            _database = await PostgresTestDatabase.CreateAsync();
            _cipher = new AesGcmStreamCipher(RandomNumberGenerator.GetBytes(32));
            _rsa = RSA.Create(2048);
        }

        [OneTimeTearDown]
        public async Task DropDatabaseAsync()
        {
            await _database.DisposeAsync();
            _cipher.Dispose();
            _rsa.Dispose();
        }

        [Test]
        public async Task BeginSignIn_StoresHashedStateAndEncryptedPkceMaterial()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            using OidcTestHttpMessageHandler handler = new(_rsa);
            using HttpClient httpClient = new(handler);
            RecordingAuthSessionIssuer sessionIssuer = new();
            OidcAuthenticationService service = CreateService(
                dbContext,
                httpClient,
                sessionIssuer);
            OidcProvider provider = await CreateProviderAsync(dbContext);
            DefaultHttpContext beginContext = new();

            string authorizationUrl = await service.BeginSignInAsync(
                provider.Slug,
                "/login",
                beginContext.Response,
                CancellationToken.None);

            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(
                new Uri(authorizationUrl).Query);
            string state = query["state"].ToString();
            string nonce = query["nonce"].ToString();
            OidcLoginState loginState = await dbContext.OidcLoginStates
                .SingleAsync(x => x.ProviderId == provider.Id);
            string codeVerifier = _cipher.DecryptString(
                Convert.FromBase64String(loginState.CodeVerifierEncrypted));
            string expectedChallenge = WebEncoders.Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
            string correlationCookie = beginContext.Response.Headers.SetCookie.ToString().ToLowerInvariant();

            Assert.Multiple(() =>
            {
                Assert.That(
                    loginState.StateHash,
                    Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)))));
                Assert.That(loginState.StateHash, Is.Not.EqualTo(state));
                Assert.That(loginState.NonceEncrypted, Is.Not.EqualTo(nonce));
                Assert.That(query["code_challenge"].ToString(), Is.EqualTo(expectedChallenge));
                Assert.That(query["code_challenge_method"].ToString(), Is.EqualTo("S256"));
                Assert.That(correlationCookie, Does.Contain("httponly"));
                Assert.That(correlationCookie, Does.Contain("secure"));
                Assert.That(correlationCookie, Does.Contain("samesite=lax"));
                Assert.That(correlationCookie, Does.Contain("path=/api/v1/auth/oidc/callback"));
            });
        }

        [Test]
        public async Task CompleteSignIn_WhenSubjectIsNotLinked_DoesNotCreateUser()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            using OidcTestHttpMessageHandler handler = new(_rsa);
            using HttpClient httpClient = new(handler);
            RecordingAuthSessionIssuer sessionIssuer = new();
            OidcAuthenticationService service = CreateService(
                dbContext,
                httpClient,
                sessionIssuer);
            OidcProvider provider = await CreateProviderAsync(dbContext);
            int userCount = await dbContext.Users.CountAsync();
            DefaultHttpContext beginContext = new();
            string authorizationUrl = await service.BeginSignInAsync(
                provider.Slug,
                "/login",
                beginContext.Response,
                CancellationToken.None);
            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(
                new Uri(authorizationUrl).Query);
            handler.IdToken = CreateIdToken(
                provider,
                query["nonce"].ToString(),
                "unknown-subject");
            DefaultHttpContext callbackContext = CreateCallbackContext(beginContext, useValidValue: true);

            OidcCallbackException exception = Assert.ThrowsAsync<OidcCallbackException>(async () =>
                await service.CompleteCallbackAsync(
                    query["state"].ToString(),
                    "authorization-code",
                    callbackContext.Request,
                    callbackContext.Response,
                    CancellationToken.None))!;
            int currentUserCount = await dbContext.Users.CountAsync();

            Assert.Multiple(() =>
            {
                Assert.That(exception.ReturnUrl, Is.EqualTo("/login?oidc=error"));
                Assert.That(exception.InnerException, Is.InstanceOf<AuthApiException>());
                Assert.That(
                    ((AuthApiException)exception.InnerException!).StatusCode,
                    Is.EqualTo(StatusCodes.Status403Forbidden));
                Assert.That(currentUserCount, Is.EqualTo(userCount));
                Assert.That(sessionIssuer.IssuedUserId, Is.Null);
            });
        }

        [Test]
        public async Task CompleteLink_WhenTokenValidationFails_ReturnsToSettingsError()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            using OidcTestHttpMessageHandler handler = new(_rsa)
            {
                IdToken = "not-a-jwt",
            };
            using HttpClient httpClient = new(handler);
            RecordingAuthSessionIssuer sessionIssuer = new();
            OidcAuthenticationService service = CreateService(dbContext, httpClient, sessionIssuer);
            OidcProvider provider = await CreateProviderAsync(dbContext);
            User user = new()
            {
                Username = $"invalid-token-{Guid.NewGuid():N}",
                PasswordPhc = "not-used",
                IsAdmin = true,
                IsDisabled = false,
            };
            await dbContext.Users.AddAsync(user);
            await dbContext.SaveChangesAsync();
            DefaultHttpContext beginContext = new();
            string authorizationUrl = await service.BeginLinkAsync(
                user.Id,
                provider.Slug,
                "/settings",
                beginContext.Response,
                CancellationToken.None);
            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(
                new Uri(authorizationUrl).Query);
            DefaultHttpContext callbackContext = CreateCallbackContext(beginContext, useValidValue: true);

            OidcCallbackException exception = Assert.ThrowsAsync<OidcCallbackException>(async () =>
                await service.CompleteCallbackAsync(
                    query["state"].ToString(),
                    "authorization-code",
                    callbackContext.Request,
                    callbackContext.Response,
                    CancellationToken.None))!;

            Assert.That(exception.ReturnUrl, Is.EqualTo("/settings?oidc=error"));
            Assert.That(await dbContext.OidcLoginStates.AnyAsync(x => x.ProviderId == provider.Id), Is.False);
            Assert.That(sessionIssuer.IssuedUserId, Is.Null);
        }

        [TestCase("https://issuer.example/new-avatar.png", "https://issuer.example/new-avatar.png")]
        [TestCase(null, null)]
        [TestCase("http://issuer.example/avatar.png", null)]
        [TestCase("https://name:password@issuer.example/avatar.png", null)]
        public async Task CompleteLink_RenewsSessionAndSignInUpdatesTheExistingProfile(string? picture, string? expectedAvatar)
        {
            const string linkedAvatar = "https://issuer.example/avatar.png";
            await using PostgresDbContext dbContext = CreateDbContext();
            using OidcTestHttpMessageHandler handler = new(_rsa);
            using HttpClient httpClient = new(handler);
            RecordingAuthSessionIssuer sessionIssuer = new();
            OidcAuthenticationService service = CreateService(
                dbContext,
                httpClient,
                sessionIssuer);
            OidcProvider provider = await CreateProviderAsync(dbContext);
            User user = new()
            {
                Username = $"flow-{Guid.NewGuid():N}",
                PasswordPhc = "not-used",
                IsAdmin = true,
                IsDisabled = false,
            };
            await dbContext.Users.AddAsync(user);
            await dbContext.SaveChangesAsync();

            DefaultHttpContext linkBeginContext = new();
            string linkAuthorizationUrl = await service.BeginLinkAsync(
                user.Id,
                provider.Slug,
                "/settings",
                linkBeginContext.Response,
                CancellationToken.None);
            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> linkQuery = QueryHelpers.ParseQuery(
                new Uri(linkAuthorizationUrl).Query);
            handler.IdToken = CreateIdToken(provider, linkQuery["nonce"].ToString(), "linked-subject", linkedAvatar);
            DefaultHttpContext linkContext = CreateCallbackContext(linkBeginContext, useValidValue: true);
            string linkReturnUrl = await service.CompleteCallbackAsync(
                linkQuery["state"].ToString(),
                "link-code",
                linkContext.Request,
                linkContext.Response,
                CancellationToken.None);
            UserExternalIdentity identity = await dbContext.UserExternalIdentities.AsNoTracking().SingleAsync(
                x => x.UserId == user.Id
                    && x.ProviderId == provider.Id
                    && x.Subject == "linked-subject");

            Assert.Multiple(() =>
            {
                Assert.That(linkReturnUrl, Is.EqualTo("/settings?oidc=linked"));
                Assert.That(sessionIssuer.IssuedUserId, Is.EqualTo(user.Id));
                Assert.That(sessionIssuer.IssueCount, Is.EqualTo(1));
                Assert.That(identity.AvatarUrl, Is.EqualTo(linkedAvatar));
            });

            DefaultHttpContext signInBeginContext = new();
            string signInAuthorizationUrl = await service.BeginSignInAsync(
                provider.Slug,
                "/login",
                signInBeginContext.Response,
                CancellationToken.None);
            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> signInQuery = QueryHelpers.ParseQuery(
                new Uri(signInAuthorizationUrl).Query);
            handler.IdToken = CreateIdToken(provider, signInQuery["nonce"].ToString(), "linked-subject", picture);
            DefaultHttpContext signInContext = CreateCallbackContext(signInBeginContext, useValidValue: true);
            string signInReturnUrl = await service.CompleteCallbackAsync(
                signInQuery["state"].ToString(),
                "sign-in-code",
                signInContext.Request,
                signInContext.Response,
                CancellationToken.None);
            string deletedCorrelationCookie = signInContext.Response.Headers.SetCookie
                .ToString()
                .ToLowerInvariant();
            GetCurrentUserQueryHandler profileQuery = new(dbContext);
            CurrentUserDto? profile = await profileQuery.Handle(new GetCurrentUserQuery(user.Id), CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(signInReturnUrl, Is.EqualTo("/login?oidc=success"));
                Assert.That(sessionIssuer.IssuedUserId, Is.EqualTo(user.Id));
                Assert.That(sessionIssuer.IssueCount, Is.EqualTo(2));
                Assert.That(profile, Is.Not.Null);
                Assert.That(profile?.AvatarUrl, Is.EqualTo(expectedAvatar));
                Assert.That(
                    deletedCorrelationCookie,
                    Does.Contain("expires=thu, 01 jan 1970 00:00:00 gmt"));
            });

            AuthApiException replayException = Assert.ThrowsAsync<AuthApiException>(async () =>
                await service.CompleteCallbackAsync(
                    signInQuery["state"].ToString(),
                    "sign-in-code",
                    signInContext.Request,
                    signInContext.Response,
                    CancellationToken.None))!;

            Assert.Multiple(() =>
            {
                Assert.That(replayException.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
                Assert.That(sessionIssuer.IssueCount, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task CompleteLinkAndProviderIdentityChange_WhenConcurrent_PreservesUsableConfiguration()
        {
            OidcProvider provider;
            User user = new()
            {
                Username = $"provider-race-{Guid.NewGuid():N}",
                PasswordPhc = "not-used",
                IsAdmin = true,
                IsDisabled = false,
            };
            string authorizationUrl;
            DefaultHttpContext beginContext = new();
            using OidcTestHttpMessageHandler handler = new(_rsa);
            using HttpClient httpClient = new(handler);
            await using (PostgresDbContext setupContext = CreateDbContext())
            {
                RecordingAuthSessionIssuer setupSessionIssuer = new();
                OidcAuthenticationService setupService = CreateService(
                    setupContext,
                    httpClient,
                    setupSessionIssuer);
                provider = await CreateProviderAsync(setupContext);
                await setupContext.Users.AddAsync(user);
                await setupContext.SaveChangesAsync();
                authorizationUrl = await setupService.BeginLinkAsync(
                    user.Id,
                    provider.Slug,
                    "/settings",
                    beginContext.Response,
                    CancellationToken.None);
            }

            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(
                new Uri(authorizationUrl).Query);
            handler.IdToken = CreateIdToken(
                provider,
                query["nonce"].ToString(),
                "provider-race-subject");
            DefaultHttpContext callbackContext = CreateCallbackContext(beginContext, useValidValue: true);
            SaveChangesBarrierInterceptor barrier = new(
                2,
                typeof(UserExternalIdentity),
                typeof(OidcProvider));
            await using PostgresDbContext linkContext = CreateDbContext(barrier);
            await using PostgresDbContext providerContext = CreateDbContext(barrier);
            OidcAuthenticationService linkService = CreateService(
                linkContext,
                httpClient,
                new RecordingAuthSessionIssuer());
            OidcProviderService providerService = new(providerContext, _cipher);
            string replacementIssuer = $"https://replacement-{Guid.NewGuid():N}.example";
            OidcProviderRequest updateRequest = new()
            {
                Name = provider.Name,
                Slug = provider.Slug,
                Issuer = replacementIssuer,
                PublicBaseUrl = provider.PublicBaseUrl,
                ClientId = "replacement-client",
                Scopes = provider.Scopes,
                IsEnabled = true,
            };

            Task<Exception?> completeLink = CaptureExceptionAsync(() => linkService.CompleteCallbackAsync(
                query["state"].ToString(),
                "authorization-code",
                callbackContext.Request,
                callbackContext.Response,
                CancellationToken.None));
            Task<Exception?> updateProvider = CaptureExceptionAsync(() => providerService.UpdateAsync(
                provider.Id,
                updateRequest,
                CancellationToken.None));
            Exception?[] results = await Task.WhenAll(completeLink, updateProvider);

            Assert.That(results.Count(result => result is null), Is.EqualTo(1));
            Exception failure = results.Single(result => result is not null)!;
            AuthApiException? conflict = failure as AuthApiException
                ?? (failure as OidcCallbackException)?.InnerException as AuthApiException;
            Assert.That(conflict?.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));

            await using PostgresDbContext verificationContext = CreateDbContext();
            OidcProvider savedProvider = await verificationContext.OidcProviders
                .AsNoTracking()
                .SingleAsync(x => x.Id == provider.Id);
            UserExternalIdentity? savedIdentity = await verificationContext.UserExternalIdentities
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProviderId == provider.Id);
            if (savedIdentity is null)
            {
                Assert.That(savedProvider.Issuer, Is.EqualTo(replacementIssuer));
            }
            else
            {
                Assert.Multiple(() =>
                {
                    Assert.That(savedIdentity.Issuer, Is.EqualTo(savedProvider.Issuer));
                    Assert.That(savedProvider.Issuer, Is.EqualTo(provider.Issuer));
                    Assert.That(savedProvider.ClientId, Is.EqualTo(provider.ClientId));
                });
            }
        }

    }
}
