# 💾 SafeWrite: ACID File Operations

> **"Writes either happen completely, or not at all."**

Modifying audio files (e.g., updating ID3 tags) is a destructive operation. If the application crashes or power fails during a write, the file can be corrupted, leading to 0-byte files or truncated audio.

ORBIT solves this with the **SafeWrite Pattern**, an implementation of **ACID** (Atomicity, Consistency, Isolation, Durability) principles for the file system.

---

## 🏗️ The Algorithm

The `SafeWriteService` (`Services/IO/SafeWriteService.cs`) wraps every destructive operation in a transaction.

### Phase 1: Preparation
1.  **Validation**: Check disk space (warn if < 100MB).
2.  **Path Normalization**: Generate absolute paths to avoid relative path ambiguity.
3.  **Temp File**: Create a unique temporary target: `track.mp3.{GUID}.tmp`.
    *   **Crucial**: The temp file is created in the **same directory** as the target. This ensures the final `File.Move` is an atomic metadata operation (pointer swap) rather than a physical copy across volumes.

### Phase 2: Journaling (Durability)
Before writing a single byte, we log our intent.

```csharp
var checkpoint = new RecoveryCheckpoint
{
    OperationType = TagWrite,
    TargetPath = "C:/Music/Track.mp3",
    StateJson = { TempPath: "C:/Music/Track.GUID.tmp", OriginalTime: "..." }
};
await _crashJournal.LogCheckpointAsync(checkpoint);
```

### Phase 3: Execution (Volatile)
1.  **Write/Copy**: The operation is performed on the `.tmp` file. The original file remains untouched.
2.  **Flush**: We force an OS flush to physical media:
    ```csharp
    stream.Flush(flushToDisk: true);
    ```
3.  **Verification**: The service checks the file integrity (e.g., Size > 0, Valid Headers).

### Phase 4: Atomic Commit (Atomicity)
The "Point of No Return". We swap the pointers.

*   **Scenario A (New File)**: simple `File.Move(temp, target)`.
*   **Scenario B (Update)**: standard `File.Replace(temp, target, backup)`.

If a crash happens *during* the swap, the OS guarantees either the old file or new file exists, never a half-swapped state.

### Phase 5: Cleanup
1.  **Complete Checkpoint**: The journal entry is deleted.
2.  **Restore Timestamps**: Original `Date Created` / `Date Modified` are restored to the new file (preserves library sorting).
3.  **Delete Temp**: Any leftover artifacts are removed.

---

## 🛡️ Recovery Scenarios

The `CrashRecoveryService` uses the journal to handle failures:

| Crash Point | State on Restart | Recovery Action |
| :--- | :--- | :--- |
| **During Write** | `Temp` exists, `Original` exists | Delete `Temp`. Operation aborted safely. |
| **After Write** | `Temp` exists (Valid), `Original` exists | Re-attempt Atomic Swap. |
| **During Swap** | `Original` missing, `Temp` exists | Complete the renaming process. |
| **After Swap** | `Original` updated, `Temp` gone | Mark checkpoint as complete. |

---

## 🧩 Usage Example

```csharp
await _safeWriteService.WriteAtomicAsync(
    targetPath: "C:/Music/Song.mp3",
    writeAction: async (tempPath) => 
    {
        // 1. Perform dangerous work on tempPath
        using var file = TagLib.File.Create(tempPath);
        file.Tag.Title = "New Title";
        file.Save();
    },
    verifyAction: async (tempPath) =>
    {
        // 2. Validate before commit
        return new FileInfo(tempPath).Length > 0;
    }
);
```

---

## ⚠️ Key Constraints
*   **Same-Volume Atomicity**: Temp files MUST be on the same volume as the target. Do not use `%TEMP%`.
*   **Timestamp Restoration**: Updating tags usually updates `LastModified`. SafeWrite explicitly reverts this to preserve "Date Added" ordering in external players.
