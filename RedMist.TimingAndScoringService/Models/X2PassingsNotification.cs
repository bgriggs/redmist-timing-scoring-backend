using MediatR;
using RedMist.TimingCommon.Models.X2;

namespace RedMist.TimingAndScoringService.Models;

public class X2PassingsNotification(List<Passing> passings) : INotification
{
    public List<Passing> Passings { get; } = passings;
}
