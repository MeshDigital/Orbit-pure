# SyntheticLibraryGenerator (PF0 / #606)

Generates deterministic synthetic ORBIT SQLite databases (`5,000` or `30,000` tracks) with realistic `LibraryEntries`, `AudioFeatures`, `CuePoints`, and `Playlists` rows using ORBIT's real EF Core schema and `DatabaseService.InitAsync()`.

## Usage

```bash
# Generate a 5k-track DB
dotnet run --project Tools/SyntheticLibraryGenerator -c Release -- --count 5000 --out ./scratch/synthetic-5k.db

# Generate a 30k-track DB
dotnet run --project Tools/SyntheticLibraryGenerator -c Release -- --count 30000 --out ./scratch/synthetic-30k.db
```

## Pointing ORBIT at the generated DB

```powershell
$env:ORBIT_DB_PATH = (Resolve-Path ./scratch/synthetic-30k.db)
dotnet run -c Release
```
