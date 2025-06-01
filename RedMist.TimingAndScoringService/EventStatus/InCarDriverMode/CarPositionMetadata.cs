using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;

namespace RedMist.TimingAndScoringService.EventStatus.InCarDriverMode;

[Reactive]
public partial class CarPositionMetadata
{
    private CarPosition? lastCarUpdate;
    private CarPosition? lastDriversCarUpdate;

    public partial string Number { get; set; } = string.Empty;
    public partial string LastTime { get; set; } = string.Empty;
    public int PositionInClass { get; set; }
    public int PositionOverall { get; set; }
    public partial string GainLoss { get; set; } = string.Empty;
    public partial string Gap { get; set; } = string.Empty;

    public bool IsDirty { get; private set; }

    public CarPositionMetadata()
    {
        PropertyChanged += (sender, args) => IsDirty = true;
    }

    public void Update(CarPosition? car)
    {
        Number = car?.Number ?? string.Empty;
        LastTime = car?.LastTime ?? string.Empty;
        PositionInClass = car?.ClassPosition ?? CarPosition.InvalidPosition;
        PositionOverall = car?.OverallPosition ?? CarPosition.InvalidPosition;

        if (lastDriversCarUpdate != null && car != null)
        {
            UpdateGapGainLoss(lastDriversCarUpdate, car);
        }
        else if (car == null)
        {
            Gap = string.Empty;
            GainLoss = string.Empty;
        }
        lastCarUpdate = car;
    }

    public void UpdateDriver(CarPosition driver)
    {
        if (lastCarUpdate != null)
        {
            UpdateGapGainLoss(driver, lastCarUpdate);
        }
        lastDriversCarUpdate = driver;
    }

    public void ClearDirty()
    {
        IsDirty = false;
    }

    public CarStatus? GetCarStatus()
    {
        if (string.IsNullOrEmpty(Number))
        {
            return null;
        }
        return new CarStatus
        {
            Number = Number,
            LastLap = LastTime,
            GainLoss = GainLoss,
            Gap = Gap
        };
    }

    private void UpdateGapGainLoss(CarPosition driver, CarPosition car)
    {
        if (car.LastLap == driver.LastLap)
        {
            // Gap
            var carTime = PositionMetadataProcessor.ParseRMTime(car.TotalTime ?? string.Empty);
            var driversTime = PositionMetadataProcessor.ParseRMTime(driver.TotalTime ?? string.Empty);
            if (carTime == default || driversTime == default)
            {
                Gap = string.Empty;
            }
            else
            {
                var gap = driversTime - carTime;
                Gap = gap.ToString(PositionMetadataProcessor.GetTimeFormat(gap));
            }
        }
        else
        {
            int laps = car.LastLap - driver.LastLap;
            if (laps < 0)
            {
                // No gap - car ahead is behind/stale
                Gap = string.Empty;
            }
            else
            {
                Gap = Math.Abs(laps) + " " + PositionMetadataProcessor.GetLapTerm(laps);
            }
        }

        // Gain/Loss
        var carLap = PositionMetadataProcessor.ParseRMTime(car.LastTime ?? string.Empty);
        var driverLap = PositionMetadataProcessor.ParseRMTime(driver.LastTime ?? string.Empty);
        if (carLap == default || driverLap == default)
        {
            GainLoss = string.Empty;
        }
        else
        {
            var gainLoss = carLap - driverLap;
            var gainLossStr = gainLoss.ToString(PositionMetadataProcessor.GetTimeFormat(gainLoss));
            if (gainLoss < TimeSpan.Zero)
            {
                gainLossStr = "-" + gainLossStr; // Indicate loss with a minus sign
            }
            GainLoss = gainLossStr;
        }
    }
}
