# Copilot Battle Test

Read-only diagnostic harness for the ORBIT copilot. Runs the Set Builder, Bridge Finder, Clash
Detector and Alternative Route finder against a **real, populated** library database and dumps the
results as JSON + a human-readable track sequence so a DJ can mentally/physically audition the mix
points.

It is strictly separate from the production cockpit: a standalone console project under `Tools/`
that only references `SLSKDONET.csproj` and opens the database **read-only** (`Mode=ReadOnly`).

## Build

```
dotnet build Tools/CopilotBattleTest/CopilotBattleTest.csproj
```

## Usage

```
dotnet run --project Tools/CopilotBattleTest \
    [--db <path>]            # library.db to read (default: %AppData%/ORBIT/library.db)
    [--minutes 60]           # Set Builder target duration
    [--bpm-variance 0.08]    # Set Builder max BPM variance (fraction)
    [--source <hash>]        # Bridge Finder source track
    [--target <hash>]        # Bridge Finder target track
    [--set <setProjectId>]   # SetListEntity id for Clash Detector + Alternative Routes
    [--out <path>]           # write JSON here (otherwise only the text log prints)
```

## Examples

```powershell
# Build a 60-minute spine and print the track sequence
dotnet run --project Tools/CopilotBattleTest -- --minutes 60

# Bridge + clash + alternative routes for a specific set, dump JSON
dotnet run --project Tools/CopilotBattleTest -- --set 3f2504e0-4f89-11d3-9a0c-0305e82c3301 --out report.json
```

## Notes

- Open the DB **read-only**; it never writes to your live library.
- If your app is running (WAL lock), point `--db` at a copy of `library.db`.
