﻿using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Captures changes to be applied to the session state.
/// </summary>
public interface ISessionStateChange : IStateChange<SessionState, SessionStatePatch>
{
}
