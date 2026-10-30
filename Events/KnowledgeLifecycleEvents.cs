using System;

namespace SLSKDONET.Models;

/// <summary>
/// Published after a live set has been recorded as a <see cref="SLSKDONET.Data.Entities.PerformanceSessionEntity"/>
/// (actual track sequence + audibles captured). The <see cref="SLSKDONET.Services.KnowledgeLifecycle.PerformanceAnalystAgent"/>
/// consumes it to reconcile the plan against reality and write <c>TransitionEvidence</c>.
/// </summary>
public record PerformanceSessionRecordedEvent(Guid SessionId);
