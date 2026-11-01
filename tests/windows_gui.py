"""CI-only UI Automation against the published application; never shipped in publish.

Requires an interactive Windows runner and pywinauto/Pillow. No private photos.
Unexecuted scenarios must not be reported as passing.
"""
import hashlib
import json
import os
from pathlib import Path
import platform
import sqlite3
import subprocess
import sys
import tempfile
import time
import traceback


def main():
    output = Path(sys.argv[2]).resolve()
    output.mkdir(parents=True, exist_ok=True)
    report = {"os": platform.platform(), "checks": [], "status": "NOT RUN"}
    process = None
    window = None
    try:
        if sys.platform != "win32" or os.environ.get("GITHUB_ACTIONS") != "true":
            raise RuntimeError("NOT RUN: requires disposable interactive Windows GitHub runner")
        from PIL import Image
        from pywinauto import Application

        db = Path(os.environ["LOCALAPPDATA"]) / "Fotoarhiv" / "catalog.sqlite"
        def rows(sql):
            with sqlite3.connect(f"{db.as_uri()}?mode=ro", uri=True) as connection:
                return connection.execute(sql).fetchall()

        # The preceding startup smoke must have created a fresh, empty catalog.
        assert rows("SELECT count(*) FROM photos") == [(0,)]
        assert rows("SELECT count(*) FROM sources") == [(0,)]
        root = Path(tempfile.mkdtemp(prefix="Fotoarhiv-GUI-"))
        first, second = root / "source-a", root / "source-b"
        (first / "nested").mkdir(parents=True)
        second.mkdir()
        for i in range(75):
            Image.new("RGB", (32, 32), (i, 10, 20)).save(first / "nested" / f"photo-{i:03d}.bmp")
        Image.new("RGB", (32, 32), (240, 50, 20)).save(first / "identical-a.bmp")
        (second / "identical-b.bmp").write_bytes((first / "identical-a.bmp").read_bytes())
        Image.new("RGB", (32, 32), (241, 50, 20)).save(second / "same-size-different.bmp")
        exif = Image.Exif()
        exif[0x8769] = {0x9003: "2020:07:08 09:10:11"}
        Image.new("RGB", (32, 32), (4, 7, 9)).save(second / "capture-2020.jpg", exif=exif)
        paths = sorted(root.rglob("*.bmp")) + sorted(root.rglob("*.jpg"))
        for p in paths:
            os.utime(p, (1704110400, 1704110400))  # File fallback in 2024, before snapshot.
        def snapshot():
            return {str(p.relative_to(root)): [hashlib.sha256(p.read_bytes()).hexdigest(), p.stat().st_mtime_ns, p.stat().st_ctime_ns] for p in paths}
        before = snapshot()
        expected = len(paths)

        def wait_for(predicate, description, seconds=60):
            deadline = time.monotonic() + seconds
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f"Process exited during {description}: {process.returncode}")
                if predicate():
                    return
                time.sleep(0.2)
            raise TimeoutError(description)

        def control(auto_id):
            return window.child_window(auto_id=auto_id)

        def text(auto_id):
            return control(auto_id).window_text()

        def check(name, assertion):
            assertion()
            report["checks"].append({"name": name, "status": "PASS"})
            (output / "gui.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

        def require(condition, description):
            if not condition:
                raise AssertionError(description)

        def start():
            nonlocal process, window
            process = subprocess.Popen([str(Path(sys.argv[1]).resolve())])
            app = Application(backend="uia").connect(process=process.pid, timeout=30)
            window = app.window(title="Фотоархив")
            window.wait("visible ready", timeout=30)
            window.maximize()
            return app

        def close():
            window.close()
            require(process.wait(timeout=15) == 0, "Nonzero exit after window close")

        app = start()
        check("empty catalog shown", lambda: require(text("PageCount") == "1 / 1" and "0 в библиотеке" in text("Summary"), "Empty gallery expected"))
        window.capture_as_image().save(output / "01-empty.png")

        def add_source(path, count):
            control("AddFolderAction").invoke()
            dialog = window.child_window(title="Выбери папку с фотографиями", control_type="Window")
            dialog.wait("visible ready", timeout=15)
            # Native Folder field observed in run 1 UIA tree; no desktop key shortcuts.
            dialog.child_window(auto_id="1152", control_type="Edit").set_edit_text(str(path))
            dialog.child_window(auto_id="1", control_type="Button").invoke()
            wait_for(lambda: text("ScanAction") == "Обновить" and text("Status").startswith("Готово:") and rows("SELECT count(*) FROM photos") == [(count,)], "Scan completion")

        add_source(first, 76)
        require(rows("SELECT count(*) FROM photos")[0][0] == 76, "First source or nested folder not indexed")
        add_source(second, expected)
        check("two sources and recursive scan through actual folder dialog", lambda: require(rows("SELECT count(*) FROM photos") == [(expected,)] and rows("SELECT count(*) FROM sources") == [(2,)], "Source/record counts"))
        check("EXIF and fallback source persisted", lambda: require(rows("SELECT date_source FROM photos WHERE path LIKE '%capture-2020.jpg'") == [("EXIF",)] and rows("SELECT DISTINCT date_source FROM photos WHERE path LIKE '%.bmp'") == [("Дата файла",)], "Date sources"))
        control("SearchBox").set_edit_text("capture-2020")
        check("search filters gallery", lambda: require("1 по фильтру" in text("Summary"), "Search count"))
        control("SearchBox").set_edit_text("")
        control("YearPicker").select("2020")
        check("year filter", lambda: require("1 по фильтру" in text("Summary"), "Year count"))
        control("YearPicker").select("Все годы")
        check("gallery pagination first page", lambda: require(text("PageCount") == "1 / 2", "First page count"))
        window.child_window(title="→", control_type="Button").invoke()
        check("gallery pagination next page", lambda: require(text("PageCount") == "2 / 2", "Second page count"))
        window.child_window(title="←", control_type="Button").invoke()
        control("SearchBox").set_edit_text("capture-2020")
        window.child_window(title="capture-2020.jpg", control_type="Text").click_input()
        info = window.child_window(title="Сведения о фотографии", control_type="Window")
        info.wait("visible", timeout=10)
        check("photo card opens with EXIF label", lambda: require(any("Источник даты: EXIF" in element.window_text() for element in info.descendants()), "Photo metadata dialog"))
        info.child_window(title="OK", control_type="Button").invoke()
        control("SearchBox").set_edit_text("")

        for button, title in [("NavSources", "Источники фотографий"), ("NavPlaces", "Места съёмки"), ("NavDuplicates", "Точные повторы"), ("NavPhotos", "Все фотографии")]:
            control(button).invoke()
            check("navigation " + button, lambda title=title: require(text("PageTitle") == title, "Navigation title"))
        control("NavDuplicates").invoke()
        groups = control("DuplicateGroups").wrapper_object().items()
        check("exact duplicate group displayed", lambda: require(len(groups) == 1, "Exactly one duplicate group expected"))
        groups[0].select()
        members = control("DuplicateMembers").wrapper_object().items()
        check("same-size different image excluded from duplicate UI", lambda: require(len(members) == 2 and all("identical-" in item.window_text() for item in members), "Duplicate members"))
        window.capture_as_image().save(output / "02-duplicates.png")
        control("NavPhotos").invoke()
        window.capture_as_image().save(output / "03-gallery.png")
        close()
        app = start()
        check("catalog survives restart", lambda: require(f"{expected} в библиотеке" in text("Summary"), "Persisted gallery count"))
        close()
        check("source names, paths, bytes, modification and creation times unchanged", lambda: require(before == snapshot() and {str(p.relative_to(root)) for p in root.rglob('*') if p.is_file()} == set(before), "Originals changed"))
        report["checks"].extend({"name": name, "status": "NOT RUN"} for name in ["physical drive disconnect", "GUI scan cancellation", "NTFS access denied", "GPS/browser user action", "offline source GUI"])
        report["status"] = "PASS"
        print("PASS: implemented UI Automation checks; NOT RUN scenarios remain explicit in gui.json")
    except Exception:
        report["status"] = "FAIL" if sys.platform == "win32" else "NOT RUN"
        report["error"] = traceback.format_exc()
        if window is not None:
            try:
                window.capture_as_image().save(output / "failure.png")
                window.print_control_identifiers(filename=str(output / "control-tree.txt"))
            except Exception:
                pass
        raise
    finally:
        if process is not None and process.poll() is None:
            process.kill()  # Only the test process started above.
            process.wait(timeout=10)
        (output / "gui.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
