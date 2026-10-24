using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// The Dashboard showed "Failed retries: 358": the crash journal's dead-letter rows, mostly
/// downloads that were merely in flight when ORBIT closed (dead-lettered on the next start and
/// never removed), and "Retry stalled downloads" reset Status = 3, a status that never exists.
/// Runs against the isolated test database (TestDatabaseSetup).
/// </summary>
[Collection(NonParallelCollection.Name)]
public class CrashRecoveryJournalDeadLetterTests
{
    private static async Task<CrashRecoveryJournal> OpenAsync()
    {
        var journal = new CrashRecoveryJournal(NullLogger<CrashRecoveryJournal>.Instance);
        await journal.InitAsync();
        return journal;
    }

    private static async Task<string> DeadLetterAsync(CrashRecoveryJournal journal, OperationType type)
    {
        var id = await journal.LogCheckpointAsync(new RecoveryCheckpoint
        {
            OperationType = type,
            TargetPath = $"C:/tmp/{Guid.NewGuid():N}.part",
            StateJson = "{}",
        });
        await journal.MarkAsDeadLetterAsync(id);
        return id;
    }

    [Fact]
    public async Task ResetDeadLetters_ResetsTheRealDeadLetterStatus()
    {
        var journal = await OpenAsync(); // not disposed: the journal's lock is static (app singleton)
        await journal.PurgeDeadLettersAsync(OperationType.Download);
        await DeadLetterAsync(journal, OperationType.Download);
        await DeadLetterAsync(journal, OperationType.Download);
        var before = (await journal.GetSystemHealthAsync()).DeadLetterCount;
        Assert.True(before >= 2);

        int reset = await journal.ResetDeadLettersAsync(batchSize: 500);

        Assert.True(reset >= 2, "ResetDeadLettersAsync matched nothing (it used to look for Status = 3)");
        Assert.Equal(0, (await journal.GetSystemHealthAsync()).DeadLetterCount);
    }

    [Fact]
    public async Task PurgeDeadLetters_RemovesOnlyThatOperationType()
    {
        var journal = await OpenAsync(); // not disposed: the journal's lock is static (app singleton)
        await journal.PurgeDeadLettersAsync(OperationType.Download);
        await journal.PurgeDeadLettersAsync(OperationType.TagWrite);
        await DeadLetterAsync(journal, OperationType.Download);
        await DeadLetterAsync(journal, OperationType.Download);
        await DeadLetterAsync(journal, OperationType.TagWrite);

        int purged = await journal.PurgeDeadLettersAsync(OperationType.Download);

        Assert.Equal(2, purged);
        Assert.Equal(1, (await journal.GetSystemHealthAsync()).DeadLetterCount);
        await journal.PurgeDeadLettersAsync(OperationType.TagWrite);
    }
}
