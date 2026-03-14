import json
import os

log_file = 'logs/log20251226.json'

if not os.path.exists(log_file):
    print("Log file not found")
    exit()

with open(log_file, 'r', encoding='utf-8') as f:
    lines = f.readlines()

print(f"Total lines: {len(lines)}")
print("Last 10 Errors/Warnings with 'Spotify':")

with open('recent_spotify_errors.txt', 'w', encoding='utf-8') as outfile:
    count = 0
    for line in reversed(lines):
        if count >= 30: break
        if '"@l":"Error"' in line or '"@l":"Warning"' in line:
            try:
                entry = json.loads(line)
                msg = entry.get('@mt', '')
                if 'Spotify' in msg:
                    outfile.write(f"[{entry.get('@t')}] {entry.get('@l')}: {msg}\n")
                    # Check for enriched properties like Status/Response if available (mapped from {Status} in log template)
                    if 'Status' in entry:
                         outfile.write(f"   Status: {entry['Status']}\n")
                    if 'Response' in entry:
                         outfile.write(f"   Response: {entry['Response']}\n")
                    
                    if '@x' in entry:
                        outfile.write(f"   Exception: {entry['@x'][:500]}...\n")
                    count += 1
            except:
                pass
