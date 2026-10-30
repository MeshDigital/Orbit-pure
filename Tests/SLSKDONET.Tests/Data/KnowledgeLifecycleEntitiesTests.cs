using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using Xunit;

namespace SLSKDONET.Tests.Data;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): round-trips the new knowledge-graph entities through the real
/// startup schema path (<c>SchemaMigratorService</c> → <c>SchemaReconciler</c>), so a fresh install
/// gets the tables and the aggregate/evidence/fingerprint/session relationships persist correctly.
/// </summary>
[Collection(NonParallelCollection.Name)]
public class KnowledgeLifecycleEntitiesTests
{
    [Fact]
    public async Task KnowledgeLifecycleEntities_RoundTrip()
    {
        using var scope = new FreshDatabaseScope();
        await Architecture.FreshDatabaseSchemaParityTests.CreateDatabaseService().InitAsync();

        var knowledge = new TransitionKnowledgeEntity
        {
            SourceTrackId = "hash-a",
            TargetTrackId = "hash-b",
            TransitionType = 1,
            AggregateScore = 88.0,
            HarmonicScore = 90.0,
            BpmScore = 85.0,
            EnergyScore = 82.0,
            VocalScore = 95.0,
            CrowdFitScore = 80.0,
            EvidenceCount = 2,
            Confidence = KnowledgeConfidence.Likely,
            Freshness = 0.9,
        };

        Guid knowledgeId;
        Guid sessionId;
        using (var context = new AppDbContext())
        {
            context.TransitionKnowledge.Add(knowledge);
            context.TransitionCandidates.Add(new TransitionCandidateEntity
            {
                SourceTrackId = "hash-c",
                TargetTrackId = "hash-d",
                CompatibilityScore = 72.0,
                DiscoverySource = "HarmonicScan",
            });
            context.DjFingerprints.Add(new DjFingerprintEntity
            {
                PreferredTransitionTypesJson = "{\"LongBlend\":0.9}",
                AvoidancePatternsJson = "[\"vocal->vocal\"]",
            });
            await context.SaveChangesAsync();

            context.TransitionEvidence.Add(new TransitionEvidenceEntity
            {
                TransitionKnowledgeId = knowledge.Id,
                Source = TransitionEvidenceSource.ManualRating,
                Score = 92.0,
                Weight = 3.0,
            });

            var session = new PerformanceSessionEntity
            {
                StartedAtUtc = DateTime.UtcNow.AddHours(-1),
                EndedAtUtc = DateTime.UtcNow,
                Venue = "Test",
            };
            session.Tracks.Add(new PerformanceSessionTrackEntity
            {
                TrackId = "hash-a",
                SequenceIndex = 0,
                WasPlanned = true,
                CrowdReaction = 80.0,
            });
            context.PerformanceSessions.Add(session);
            await context.SaveChangesAsync();

            knowledgeId = knowledge.Id;
            sessionId = session.Id;
        }

        using (var context = new AppDbContext())
        {
            var loaded = await context.TransitionKnowledge.FindAsync(knowledgeId);
            Assert.NotNull(loaded);
            Assert.Equal(KnowledgeConfidence.Likely, loaded.Confidence);
            Assert.Equal(0.9, loaded.Freshness, 3);

            var evidence = await context.TransitionEvidence
                .Where(e => e.TransitionKnowledgeId == knowledgeId)
                .ToListAsync();
            Assert.Single(evidence);
            Assert.Equal(TransitionEvidenceSource.ManualRating, evidence[0].Source);
            Assert.Equal(3.0, evidence[0].Weight, 3);

            var loadedSession = await context.PerformanceSessions
                .Include(s => s.Tracks)
                .FirstOrDefaultAsync(s => s.Id == sessionId);
            Assert.NotNull(loadedSession);
            Assert.Single(loadedSession.Tracks);
            Assert.Equal(80.0, loadedSession.Tracks.First().CrowdReaction!.Value, 3);
        }
    }
}
