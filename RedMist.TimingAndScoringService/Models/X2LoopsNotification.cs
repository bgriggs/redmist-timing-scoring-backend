using MediatR;
using RedMist.TimingCommon.Models.X2;

namespace RedMist.TimingAndScoringService.Models;

public class X2LoopsNotification(List<Loop> loops) : INotification
{
    public List<Loop> Loops { get; } = loops;
}
