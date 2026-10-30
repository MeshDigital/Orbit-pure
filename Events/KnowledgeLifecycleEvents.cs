using System;

namespace SLSKDONET.Models;

/// <summary>
/// Published after a live set has been recorded as a <see cref="SLSKDONET.Data.Entities.PerformanceSessionEntity"/>
/// (actual track sequence + audibles captured). The <see cref="SLSKDONET.Services.KnowledgeLifecycle.PerformanceAnalystAgent"/>
/// consumes it to reconcile the plan against reality and write <c>TransitionEvidence</c>.
/// </summary>
public record PerformanceSessionRecordedEvent(Guid SessionId);

/// <summary>
/// Requests that a <see cref="SLSKDONET.Data.Entities.SetListEntity"/> (SetProject) be compiled for
/// export, with the knowledge graph's confidence/freshness metadata attached. When
/// <paramref name="FilesAndXml"/> is true the set is copied to USB/folder and the Rekordbox XML is
/// written; otherwise only the XML is produced.
/// </summary>
public record SetExportRequestedEvent(Guid SetProjectId, string DestinationRoot, bool FilesAndXml = true);
