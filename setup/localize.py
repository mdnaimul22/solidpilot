"""Local adaptation for this machine (run once).

The ERP web app already listens on 0.0.0.0:5000, which is the execution server's
hardcoded default. This moves that base address to 5050, in both the shipped
binary and the source, and writes the adapter's .env so both sides agree.
"""

import shutil
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
EXE = REPO / "execution/solidworks/SolidworksExecution/bin/Debug/SolidworksExecution.exe"
PROG = REPO / "execution/solidworks/SolidworksExecution/Program.cs"
ENV = REPO / "adapters/.env"

OLD, NEW = "http://localhost:5000/", "http://localhost:5050/"

# --- 1. the shipped binary (same-length replacement, so no metadata shifts) ---
data = EXE.read_bytes()
hits = []
start = 0
while True:
    i = data.find(OLD.encode("utf-16-le"), start)
    if i < 0:
        break
    hits.append(i)
    start = i + 1

if len(hits) == 1:
    backup = EXE.with_suffix(".exe.orig-port5000")
    if not backup.exists():
        shutil.copy2(EXE, backup)
    EXE.write_bytes(data.replace(OLD.encode("utf-16-le"), NEW.encode("utf-16-le")))
    print(f"exe   : patched offset {hits[0]} -> {NEW}  (backup: {backup.name})")
elif data.count(NEW.encode("utf-16-le")) == 1:
    print(f"exe   : already patched -> {NEW}")
else:
    sys.exit(f"exe   : expected 1 occurrence of {OLD}, found {len(hits)}")

# --- 2. the source, so a future rebuild keeps the port ---
src = PROG.read_text(encoding="utf-8")
if NEW in src:
    print("source: already patched")
elif OLD in src:
    PROG.write_text(src.replace(OLD, NEW), encoding="utf-8")
    print(f"source: {OLD} -> {NEW}")
else:
    sys.exit("source: base address literal not found")

# --- 3. the adapter side ---
ENV.write_text(
    "# Port 5000 is taken by the ERP web app on this machine; the execution server\n"
    "# listens on 5050 (see Program.cs and the patched exe). Keep both in sync.\n"
    "EXECUTION_BASE_URL=http://localhost:5050\n",
    encoding="utf-8",
)
print(f"env   : {ENV}")
