﻿using Microsoft.EntityFrameworkCore;
using System.Configuration;
using TeslaSolarCharger.Model.Contracts;
using TeslaSolarCharger.Server.Contracts;
using TeslaSolarCharger.Server.Resources;
using TeslaSolarCharger.Server.Services.ApiServices.Contracts;
using TeslaSolarCharger.Shared;
using TeslaSolarCharger.Shared.Dtos.Contracts;
using TeslaSolarCharger.Shared.Dtos.IndexRazor.CarValues;
using TeslaSolarCharger.Shared.Dtos.IndexRazor.PvValues;
using TeslaSolarCharger.Shared.Dtos.Settings;
using TeslaSolarCharger.Shared.Enums;
using TeslaSolarCharger.Shared.Resources;

namespace TeslaSolarCharger.Server.Services.ApiServices;

public class IndexService : IIndexService
{
    private readonly ILogger<IndexService> _logger;
    private readonly ISettings _settings;
    private readonly ITeslamateContext _teslamateContext;
    private readonly IChargingCostService _chargingCostService;
    private readonly ToolTipTextKeys _toolTipTextKeys;

    public IndexService(ILogger<IndexService> logger, ISettings settings, ITeslamateContext teslamateContext,
        IChargingCostService chargingCostService, ToolTipTextKeys toolTipTextKeys)
    {
        _logger = logger;
        _settings = settings;
        _teslamateContext = teslamateContext;
        _chargingCostService = chargingCostService;
        _toolTipTextKeys = toolTipTextKeys;
    }

    public DtoPvValues GetPvValues()
    {
        _logger.LogTrace("{method}()", nameof(GetPvValues));
        return new DtoPvValues()
        {
            GridPower = _settings.Overage,
            InverterPower = _settings.InverterPower,
            HomeBatteryPower = _settings.HomeBatteryPower,
            HomeBatterySoc = _settings.HomeBatterySoc,
            CarCombinedChargingPowerAtHome = _settings.Cars.Select(c => c.CarState.ChargingPowerAtHome).Sum(),
        };
    }

    public async Task<List<DtoCarBaseStates>> GetCarBaseStatesOfEnabledCars()
    {
        _logger.LogTrace("{method}()", nameof(GetCarBaseStatesOfEnabledCars));
        var enabledCars = GetEnabledCars();
        var carBaseValues = new List<DtoCarBaseStates>();
        foreach (var enabledCar in enabledCars)
        {
            var dtoCarBaseValues = new DtoCarBaseStates()
            {
                CarId = enabledCar.Id,
                NameOrVin = enabledCar.CarState.Name,
                StateOfCharge = enabledCar.CarState.SoC,
                StateOfChargeLimit = enabledCar.CarState.SocLimit,
                HomeChargePower = enabledCar.CarState.ChargingPowerAtHome,
                PluggedInAtHome = enabledCar.CarState is { IsHomeGeofence: true, PluggedIn: true },
                IsHome = enabledCar.CarState.PluggedIn == true,
                IsAutoFullSpeedCharging = enabledCar.CarState.AutoFullSpeedCharge,
            };
            if (string.IsNullOrEmpty(dtoCarBaseValues.NameOrVin))
            {
                dtoCarBaseValues.NameOrVin = await GetVinByCarId(enabledCar.Id).ConfigureAwait(false);
            }
            dtoCarBaseValues.DtoChargeSummary = await _chargingCostService.GetChargeSummary(enabledCar.Id).ConfigureAwait(false);

            carBaseValues.Add(dtoCarBaseValues);
            
        }
        return carBaseValues;
    }

    public Dictionary<int, DtoCarBaseSettings> GetCarBaseSettingsOfEnabledCars()
    {
        _logger.LogTrace("{method}()", nameof(GetCarBaseSettingsOfEnabledCars));
        var enabledCars = GetEnabledCars();

        return enabledCars.ToDictionary(enabledCar => enabledCar.Id, enabledCar => new DtoCarBaseSettings()
        {
            CarId = enabledCar.Id,
            ChargeMode = enabledCar.CarConfiguration.ChargeMode,
            MinimumStateOfCharge = enabledCar.CarConfiguration.MinimumSoC,
            LatestTimeToReachStateOfCharge = enabledCar.CarConfiguration.LatestTimeToReachSoC,
        });
    }

    public void UpdateCarBaseSettings(DtoCarBaseSettings carBaseSettings)
    {
        var carConfiguration = _settings.Cars
            .Where(c => c.Id == carBaseSettings.CarId)
            .Select(c => c.CarConfiguration).First();
        carConfiguration.ChargeMode = carBaseSettings.ChargeMode;
        carConfiguration.MinimumSoC = carBaseSettings.MinimumStateOfCharge;
        carConfiguration.LatestTimeToReachSoC = carBaseSettings.LatestTimeToReachStateOfCharge;
    }

    public Dictionary<string, string> GetToolTipTexts()
    {
        return new Dictionary<string, string>()
        {
            { _toolTipTextKeys.InverterPower, "Power your inverter currently delivers." },
            { _toolTipTextKeys.GridPower, "Power at your grid point. Green: Power feeding into grid; Red: Power consuming from grid" },
            { _toolTipTextKeys.HomeBatterySoC, "State of charge of your home battery." },
            { _toolTipTextKeys.HomeBatteryPower, "Power of your car battery. Green: Battery is charging; Red: Battery is discharging" },
            { _toolTipTextKeys.CombinedChargingPower, "Power sum of all cars charging at home." },
            { _toolTipTextKeys.CarName, "Name configured in your car (or VIN if no name defined)." },
            { _toolTipTextKeys.CarSoc, "State of charge" },
            { _toolTipTextKeys.CarSocLimit, "SoC Limit (configured in the car or in the Tesla App)" },
            { _toolTipTextKeys.CarChargingPowerHome, "Power your car is currently charging at home" },
            { _toolTipTextKeys.CarChargedSolarEnergy, "Total charged solar energy" },
            { _toolTipTextKeys.CarChargedGridEnergy, "Total charged grid energy" },
            { _toolTipTextKeys.CarChargeCost, "Total Charge cost. Note: The charge costs are also autoupdated in the charges you find in TeslaMate. This update can take up to 10 minutes after a charge is completed." },
            { _toolTipTextKeys.CarAtHome, "Your car is in your defined GeoFence" },
            { _toolTipTextKeys.CarPluggedIn, "Your car is plugged in at home" },
            { _toolTipTextKeys.CarChargeMode, "ChargeMode of your car\r\n" +
                                              $"{ChargeMode.MaxPower.ToFriendlyString()}: Your car will charge with the maximum available power.\r\n" +
                                              $"{ChargeMode.PvOnly.ToFriendlyString()}: Your car will charge with solar power only despite you configured a min SoC in combination with a date when this soc should be reached.\r\n" +
                                              $"{ChargeMode.PvAndMinSoc.ToFriendlyString()}: Your car will charge to the configured Min SoC with maximum available power, then it will continue to charge based on available solar power."},
        };
    }

    private List<Car> GetEnabledCars()
    {
        _logger.LogTrace("{method}()", nameof(GetEnabledCars));
        return _settings.Cars.Where(c => c.CarConfiguration.ShouldBeManaged == true).ToList();
    }

    

    public async Task<string?> GetVinByCarId(int carId)
    {
        _logger.LogTrace("{method}({carId})", nameof(GetVinByCarId), carId);
        return await _teslamateContext.Cars
            .Where(c => c.Id == carId)
            .Select(c => c.Vin).FirstAsync().ConfigureAwait(false);
    }
}
