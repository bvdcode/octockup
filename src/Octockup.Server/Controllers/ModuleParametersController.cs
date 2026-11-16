// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions;
using EasyExtensions.AspNetCore.Extensions;
using EasyExtensions.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Octockup.Server.Abstractions;
using Octockup.Server.Handlers.Modules;
using Octockup.Server.Helpers;
using Octockup.Server.Models.Dto;
using Octockup.Server.Models.Enums;

namespace Octockup.Server.Controllers
{
    [ApiController]
    [Authorize]
    [Route("/api/v1/modules/{moduleId:guid}/parameters")]
    public class ModuleParametersController(IMediator mediator, ILogger<ModuleParametersController> logger) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Get(Guid moduleId, CancellationToken cancellationToken)
        {
            Response.Headers.CacheControl = "no-store";
            ModuleParametersDto? parameters = await mediator.Send(
                new GetModuleParametersQuery(User.GetUserId(), moduleId), cancellationToken);
            return parameters is null ? NotFound() : Ok(parameters);
        }

        [HttpPatch]
        public async Task<IActionResult> Update(Guid moduleId,
            [FromBody] Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            if (parameters.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value is null))
            {
                return BadRequest();
            }
            bool updated = await mediator.Send(new UpdateModuleParametersCommand(
                User.GetUserId(), moduleId, parameters), cancellationToken);
            return updated ? NoContent() : NotFound();
        }

        [HttpPost("test")]
        public async Task<IActionResult> Test(Guid moduleId,
            [FromBody] Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            if (parameters.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value is null))
            {
                return BadRequest();
            }
            PreparedModuleConnection? connection;
            try
            {
                connection = await mediator.Send(new PrepareModuleConnectionQuery(
                    User.GetUserId(), moduleId, parameters), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Connection parameters could not be prepared for module {ModuleId}.", moduleId);
                return this.ApiBadRequest("Invalid connection parameters.");
            }
            if (connection is null)
            {
                return NotFound();
            }
            return connection.Destination switch
            {
                ModuleDestination.Source => await TestHelpers.TestSourceAsync(this, connection.Provider, logger),
                ModuleDestination.Target when connection.Provider is IBackupStorage storage =>
                    await TestHelpers.TestStorageAsync(this, storage, logger),
                _ => this.ApiBadRequest("Unsupported module destination."),
            };
        }
    }
}
