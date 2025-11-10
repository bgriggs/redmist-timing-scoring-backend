using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;

namespace RedMist.EventProcessor.EventStatus.InCarDriverMode;

public class CarSet
{
    public CarPositionMetadata CarAhead { get; } = new CarPositionMetadata();
    public CarPositionMetadata CarAheadOutOfClass { get; } = new CarPositionMetadata();
    public CarPositionMetadata DriversCar { get; } = new CarPositionMetadata();
    public CarPositionMetadata CarBehind { get; } = new CarPositionMetadata();

    public bool IsDirty => CarAhead.IsDirty || CarAheadOutOfClass.IsDirty || DriversCar.IsDirty || CarBehind.IsDirty;


    public void UpdateDriver(CarPosition driver)
    {
        DriversCar.Update(driver);
        CarAhead.UpdateDriver(driver);
        CarAheadOutOfClass.UpdateDriver(driver);
        CarBehind.UpdateDriver(driver);
    }

    public void ClearDirtyFlags()
    {
        CarAhead.ClearDirty();
        CarAheadOutOfClass.ClearDirty();
        DriversCar.ClearDirty();
        CarBehind.ClearDirty();
    }

    public InCarPayload GetPayloadPartial()
    {
        try
        {
            return new InCarPayload
            {
                CarNumber = DriversCar.Number,
                PositionInClass = DriversCar.PositionInClass.ToString(),
                PositionOverall = DriversCar.PositionOverall.ToString(),
                Cars = [CarAhead.GetCarStatus(), CarAheadOutOfClass.GetCarStatus(), DriversCar.GetCarStatus(), CarBehind.GetCarStatus()]
            };
        }
        finally
        {
            ClearDirtyFlags();
        }
    }

}
