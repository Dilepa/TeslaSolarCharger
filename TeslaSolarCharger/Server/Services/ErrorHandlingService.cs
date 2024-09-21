﻿using AutoMapper.QueryableExtensions;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Globalization;
using TeslaSolarCharger.Model.Contracts;
using TeslaSolarCharger.Model.Entities.TeslaSolarCharger;
using TeslaSolarCharger.Server.Contracts;
using TeslaSolarCharger.Server.Dtos;
using TeslaSolarCharger.Server.Resources.PossibleIssues.Contracts;
using TeslaSolarCharger.Server.Services.Contracts;
using TeslaSolarCharger.Shared.Contracts;
using TeslaSolarCharger.Shared.Dtos;
using TeslaSolarCharger.Shared.Dtos.ChargingCost;
using TeslaSolarCharger.SharedBackend.MappingExtensions;

namespace TeslaSolarCharger.Server.Services;

public class ErrorHandlingService(ILogger<ErrorHandlingService> logger,
    IBackendApiService backendApiService,
    IIssueKeys issueKeys,
    ITelegramService telegramService,
    ITeslaSolarChargerContext context,
    IDateTimeProvider dateTimeProvider,
    IConfigurationWrapper configurationWrapper,
    IMapperConfigurationFactory mapperConfigurationFactory) : IErrorHandlingService
{
    public async Task<Fin<List<DtoLoggedError>>> GetActiveLoggedErrors()
    {
        logger.LogTrace("{method}()", nameof(GetActiveLoggedErrors));
        var mapper = mapperConfigurationFactory.Create(cfg =>
        {
            cfg.CreateMap<LoggedError, DtoLoggedError>()
                .ForMember(d => d.Occurrences, opt => opt.MapFrom(s => new List<DateTime>(){s.StartTimeStamp}.Concat(s.FurtherOccurrences)))
                ;
        });
        try
        {
            var errors = await context.LoggedErrors
                .Where(e => e.EndTimeStamp == default)
                .ProjectTo<DtoLoggedError>(mapper)
                .ToListAsync();
            return Fin<List<DtoLoggedError>>.Succ(errors);
        }
        catch (Exception ex)
        {
            return Fin<List<DtoLoggedError>>.Fail(Error.New(ex));
        }

    }

    public async Task HandleError(string source, string methodName, string message, string issueKey, string? vin,
        string? stackTrace)
    {
        logger.LogTrace("{method}({source}, {methodName}, {message}, {issueKey}, {vin}, {stackTrace})",
            nameof(HandleError), source, methodName, message, issueKey, vin, stackTrace);
        await backendApiService.PostErrorInformation(source, methodName, message, issueKey, vin, stackTrace);
        var existingError = await context.LoggedErrors
            .Where(e => e.IssueKey == issueKey
                        && e.Vin == vin
                        && e.EndTimeStamp == null)
            .FirstOrDefaultAsync();
        if (existingError == default)
        {
            var error = new LoggedError()
            {
                StartTimeStamp = dateTimeProvider.UtcNow(),
                IssueKey = issueKey,
                Vin = vin,
                Source = source,
                MethodName = methodName,
                Message = message,
                StackTrace = stackTrace,
            };
            context.LoggedErrors.Add(error);
        }
        else
        {
            existingError.FurtherOccurrences.Add(dateTimeProvider.UtcNow());
        }

        
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task HandleErrorResolved(string issueKey, string? vin)
    {
        logger.LogTrace("{method}({issueKey}, {vin})", nameof(HandleErrorResolved), issueKey, vin);
        var openError = context.LoggedErrors
            .FirstOrDefault(e => e.IssueKey == issueKey
                                 && e.Vin == vin
                                 && e.EndTimeStamp == null);
        if (openError == null)
        {
            return;
        }
        openError.EndTimeStamp = dateTimeProvider.UtcNow();
        await context.SaveChangesAsync();
    }

    public async Task SendTelegramMessages()
    {
        var openErrors = await context.LoggedErrors
            .Where(e => e.EndTimeStamp == null && e.TelegramNotificationSent == false)
            .ToListAsync();
        foreach (var error in openErrors)
        {
            if (!TelegramEnabledIssueKeys.Any(i => error.IssueKey.StartsWith(i)))
            {
                continue;
            }

            var errorText = $"[{error.StartTimeStamp.ToLocalTime()}] Error with key {error.IssueKey} ";
            if (!string.IsNullOrEmpty(error.Vin))
            {
                errorText += $"for car {error.Vin} ";
            }
            errorText += $"in {error.Source}.{error.MethodName}: {error.Message}";
            if (configurationWrapper.SendStackTraceToTelegram() && !string.IsNullOrEmpty(error.StackTrace))
            {
                errorText += $"\r\nStack Trace: {error.StackTrace}";
            }
            var statusCode = await telegramService.SendMessage(errorText);
            if (((int)statusCode >= 200) && ((int)statusCode <= 299))
            {
                error.TelegramNotificationSent = true;
            }
        }
        var closedErrors = await context.LoggedErrors
            .Where(e => e.EndTimeStamp != null && e.TelegramResolvedMessageSent == false && e.TelegramNotificationSent)
            .ToListAsync();
        foreach (var error in closedErrors)
        {
            if (!TelegramEnabledIssueKeys.Any(i => error.IssueKey.StartsWith(i)))
            {
                continue;
            }

            var resolvedText = $"Error with key {error.IssueKey} ";
            if (string.IsNullOrEmpty(error.Vin))
            {
                resolvedText += $"and VIN {error.Vin} ";
            }
            resolvedText += $"from {error.StartTimeStamp.ToLocalTime()} has been resolved after {error.FurtherOccurrences.Count + 1} occurrences at {error.EndTimeStamp?.ToLocalTime()}";
            var statusCode = await telegramService.SendMessage(resolvedText);
            if (((int)statusCode >= 200) && ((int)statusCode <= 299))
            {
                error.TelegramResolvedMessageSent = true;
            }
        }

        await context.SaveChangesAsync();
    }

    private System.Collections.Generic.HashSet<string> TelegramEnabledIssueKeys =>
    [
        issueKeys.GridPowerNotAvailable,
        issueKeys.InverterPowerNotAvailable,
        issueKeys.HomeBatterySocNotAvailable,
        issueKeys.HomeBatterySocNotPlausible,
        issueKeys.HomeBatteryPowerNotAvailable,
        issueKeys.FleetApiTokenNotRequested,
        issueKeys.FleetApiTokenUnauthorized,
        issueKeys.FleetApiTokenMissingScopes,
        issueKeys.FleetApiTokenRequestExpired,
        issueKeys.FleetApiTokenNotReceived,
        issueKeys.FleetApiTokenExpired,
        issueKeys.FleetApiTokenNoApiRequestsAllowed,
        issueKeys.GetVehicle,
        issueKeys.GetVehicleData,
        issueKeys.CarStateUnknown,
        issueKeys.UnhandledCarStateRefresh,
        issueKeys.FleetApiNonSuccessStatusCode,
        issueKeys.FleetApiNonSuccessResult,
        issueKeys.UnsignedCommand,
        issueKeys.FleetApiTokenRefreshNonSuccessStatusCode,
    ];
}
