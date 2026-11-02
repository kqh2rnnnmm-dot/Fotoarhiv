"""Disposable Windows runner verification, evidence and complete publish packaging."""
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def run_step(name, command, evidence, report):
    entry = {"name": name, "command": command, "started_utc": datetime.now(timezone.utc).isoformat(), "status": "FAIL"}
    report["steps"].append(entry)
    try:
        with (evidence / f"{name}.log").open("w", encoding="utf-8") as log:
            result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, text=True, encoding="utf-8")
        entry["exit_code"] = result.returncode
        if result.returncode:
            raise RuntimeError(f"{name} failed ({result.returncode}); see {name}.log")
        entry["status"] = "PASS"
    finally:
        entry["finished_utc"] = datetime.now(timezone.utc).isoformat()
        write_json(evidence / "run.json", report)


def package_publish(publish, archive_base, evidence, report):
    required = {"restore", "build", "xunit", "publish", "startup", "gui"}
    passed = {s["name"] for s in report["steps"] if s["status"] == "PASS"}
    if not required <= passed:
        raise RuntimeError("Refusing ZIP: required Windows checks did not pass")
    for name in ["Fotoarhiv.exe", "e_sqlite3.dll", "coreclr.dll", "wpfgfx_cor3.dll"]:
        if not (publish / name).is_file():
            raise RuntimeError(f"Publish incomplete: {name}")
    manifest = {p.relative_to(publish).as_posix(): {"bytes": p.stat().st_size, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(publish.rglob("*")) if p.is_file()}
    write_json(evidence / "publish-manifest.json", manifest)
    archive = Path(shutil.make_archive(str(archive_base), "zip", publish))
    write_json(evidence / "archive.json", {"name": archive.name, "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "bytes": archive.stat().st_size})
    return archive


def main():
    evidence = Path("TestResults")
    evidence.mkdir(exist_ok=True)
    report = {"status": "NOT RUN", "p0_status": "OPEN: manual Windows 10/11 and actual DPI checklist required", "os": platform.platform(), "windows_version": platform.win32_ver(), "steps": [], "run_url": None, "commit": os.environ.get("GITHUB_SHA")}
    if all(os.environ.get(k) for k in ["GITHUB_SERVER_URL", "GITHUB_REPOSITORY", "GITHUB_RUN_ID"]):
        report["run_url"] = f"{os.environ['GITHUB_SERVER_URL']}/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
    write_json(evidence / "run.json", report)
    if sys.platform != "win32" or os.environ.get("GITHUB_ACTIONS") != "true":
        raise SystemExit("NOT RUN: requires a disposable Windows GitHub Actions runner")
    try:
        # Do not touch an existing catalog, even if this script is invoked accidentally.
        db = Path(os.environ["LOCALAPPDATA"]) / "Fotoarhiv" / "catalog.sqlite"
        if db.exists():
            raise RuntimeError("Refusing to test against an existing catalog; use a fresh runner")
        report["status"] = "RUNNING"
        run_step("environment", ["dotnet", "--info"], evidence, report)
        run_step("restore", ["dotnet", "restore", "Fotoarhiv.slnx"], evidence, report)
        run_step("build", ["dotnet", "build", "Fotoarhiv.slnx", "-c", "Release", "--no-restore"], evidence, report)
        run_step("xunit", ["dotnet", "test", "tests/Fotoarhiv.Tests/Fotoarhiv.Tests.csproj", "-c", "Release", "--no-build", "--logger", "trx;LogFileName=windows-tests.trx", "--results-directory", str(evidence)], evidence, report)
        trx = ET.parse(evidence / "windows-tests.trx")
        counters = trx.find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
        if counters is None or int(counters.attrib["passed"]) < 24 or int(counters.attrib["passed"]) != int(counters.attrib["total"]):
            raise RuntimeError("Expected all 24+ xUnit tests to pass without skips")
        report["xunit"] = counters.attrib
        run_step("publish", ["dotnet", "publish", "src/Fotoarhiv.App/Fotoarhiv.App.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-o", "publish"], evidence, report)
        run_step("startup", [sys.executable, "tests/windows_smoke.py", "publish/Fotoarhiv.exe"], evidence, report)
        run_step("gui", [sys.executable, "tests/windows_gui.py", "publish/Fotoarhiv.exe", str(evidence / "gui")], evidence, report)
        package_publish(Path("publish"), Path("Fotoarhiv_Windows_x64_TEST_0_2_2"), evidence, report)
        report["status"] = "PASS"
    except Exception as error:
        report["status"] = "FAIL"
        report["error"] = str(error)
        raise
    finally:
        write_json(evidence / "run.json", report)


if __name__ == "__main__":
    main()
