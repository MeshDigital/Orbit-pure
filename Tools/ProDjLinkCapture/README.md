# Pro DJ Link Capture

Standalone UDP packet sniffer for mapping the real Pioneer Pro DJ Link wire format. Binds UDP
50000 (read-only, `ReuseAddress`) and dumps every raw packet as hex + length — independent of the
ORBIT app.

## Build & run (on the machine connected to the Pioneer hardware)

```powershell
dotnet run --project Tools/ProDjLinkCapture -- --seconds 60 --out capture.txt
```

Flags:
- `--port 50000` (default 50000 keep-alive; use `50002` for status/beat-grid)
- `--seconds 60` — stop after N seconds (default: run until Ctrl+C)
- `--out capture.txt` — append hex to a file

## What to do

1. Run it on the machine on the same Cisco switch as the decks.
2. Power on the CDJs/XDJs + mixer.
3. Copy the `[N bytes] HEX...` lines and paste them back so the `ProDjLinkPacketParser` offsets can
   be mapped against the real bytes.
