"""Opt-in live guest acceptance: python tests/live_jobs.py --vm build-windows-dev."""
import argparse
import json
import pathlib
import subprocess
import tempfile
import time
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--vm", required=True)
args = parser.parse_args()
ROOT = pathlib.Path(__file__).resolve().parents[1]

with tempfile.TemporaryDirectory(prefix="winix-live-") as directory:
    root = pathlib.Path(directory)
    subprocess.run(["git", "init", "-q", directory], check=True)
    tasks = {
        "success": {"script": "Write-Output 'unicode é中😀'; 1..3000 | ForEach-Object { Write-Output ('line ' + $_ + ' ' + ('x' * 32)) }; 'artifact' | Set-Content result.txt; Start-Sleep -Seconds 3", "artifacts": ["result.txt"]},
        "failure": {"script": "Write-Output 'expected failure'; exit 17"},
        "cancel": {"script": "$p = Start-Process powershell.exe -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 90' -PassThru; $p.Id | Set-Content child.txt; Write-Output 'child started'; Start-Sleep -Seconds 90"},
        "timeout": {"script": "Start-Sleep -Seconds 90", "timeoutSeconds": 3},
        "verify": {"script": "$child = [int](Get-Content child.txt); if (Get-Process -Id $child -ErrorAction SilentlyContinue) { throw 'Cancelled child survived' }; Write-Output 'child terminated'"},
    }
    for task in tasks.values():
        task["visualStudio"] = False
    config = root / "winix.yaml"
    config.write_text(json.dumps({"version": 1, "workspace": "live-jobs", "pipeline": [], "tasks": tasks}))

    def cli(*words, expected=0):
        result = subprocess.run([str(ROOT / "winix"), *words, "--vm", args.vm, "--config", str(config), "--json"], capture_output=True, text=True, timeout=120)
        assert result.returncode == expected, (words, result.returncode, result.stdout, result.stderr)
        return json.loads(result.stdout)

    cli("sync", directory)
    job = cli("run", "success")
    # Every call is a new process/SSH connection; submission is intentionally detached.
    assert cli("job", "wait", job["id"])["state"] == "succeeded"
    offset = 0
    logs = ""
    pages = 0
    while True:
        page = cli("job", "logs", job["id"], "--offset", str(offset))
        pages += 1
        logs += page["text"]
        if page["end"]:
            break
        assert page["nextOffset"] > offset
        offset = page["nextOffset"]
    assert pages > 1 and "line 3000 " in logs and "unicode é中😀" in logs
    archive = cli("job", "artifacts", job["id"], "--output", directory)["path"]
    with zipfile.ZipFile(archive) as contents:
        assert "artifacts/result.txt" in contents.namelist()
        assert "request.json" not in contents.namelist()
    job = cli("run", "failure")
    failed = cli("job", "wait", job["id"], expected=1)
    assert failed["state"] == "failed" and failed["exitCode"] == 17
    job = cli("run", "cancel")
    until = time.monotonic() + 30
    while "child started" not in cli("job", "logs", job["id"])["text"]:
        assert time.monotonic() < until, "Child did not start"
        time.sleep(1)
    assert cli("sync", directory, expected=3)["error"] == "busy"
    cli("job", "cancel", job["id"])
    assert cli("job", "wait", job["id"], expected=1)["state"] == "cancelled"
    job = cli("run", "verify")
    assert cli("job", "wait", job["id"])["state"] == "succeeded"
    job = cli("run", "timeout")
    assert cli("job", "wait", job["id"], expected=1)["state"] == "timedOut"
    print("Live jobs passed: detached execution, paged logs, artifacts, failure exit code, busy protection, cancellation with child cleanup, timeout")
