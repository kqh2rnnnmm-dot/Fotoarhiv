"""Real WPF UI Automation and native screenshots on a disposable Windows CI desktop."""
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
    output = Path(sys.argv[2]).resolve(); output.mkdir(parents=True, exist_ok=True)
    shots = output/'screenshots'; shots.mkdir(exist_ok=True)
    report = {'os':platform.platform(), 'checks':[], 'screenshots':[], 'status':'NOT RUN'}
    process = None; window = None; old_mode = None; offline = None; second = None
    try:
        if sys.platform != 'win32' or os.environ.get('GITHUB_ACTIONS') != 'true':
            raise RuntimeError('NOT RUN: requires disposable interactive Windows GitHub runner')
        import win32api, win32con, win32gui
        from PIL import Image, ImageGrab
        from pywinauto import Application
        from pywinauto.keyboard import send_keys
        from ui_fixtures import create_catalog_samples
        old_mode = win32api.EnumDisplaySettings(None, win32con.ENUM_CURRENT_SETTINGS)
        requested = win32api.EnumDisplaySettings(None, win32con.ENUM_CURRENT_SETTINGS)
        requested.PelsWidth = 2560; requested.PelsHeight = 1440
        requested.Fields = win32con.DM_PELSWIDTH | win32con.DM_PELSHEIGHT
        change_result = win32api.ChangeDisplaySettings(requested, 0)
        report['display_setup'] = {'requested':[2560,1440], 'result':change_result, 'actual':[win32api.GetSystemMetrics(0),win32api.GetSystemMetrics(1)]}
        if change_result != 0 or win32api.GetSystemMetrics(0) < 1940 or win32api.GetSystemMetrics(1) < 1120:
            raise RuntimeError('Desktop must fit a 1920x1080 client plus the native window frame')
        db = Path(os.environ['LOCALAPPDATA'])/'Fotoarhiv/catalog.sqlite'
        def rows(sql):
            with sqlite3.connect(f'{db.as_uri()}?mode=ro',uri=True) as connection: return connection.execute(sql).fetchall()
        assert rows('SELECT count(*) FROM photos') == [(0,)] and rows('SELECT count(*) FROM sources') == [(0,)]
        root = Path(tempfile.mkdtemp(prefix='Fotoarhiv-UI003-')).resolve()
        first, second, paths = create_catalog_samples(root)
        expected = len(paths)
        def snapshot():
            return {str(p.relative_to(root)):[hashlib.sha256(p.read_bytes()).hexdigest(),p.stat().st_mtime_ns,getattr(p.stat(),'st_birthtime_ns',p.stat().st_ctime_ns)] for p in paths}
        before = snapshot()
        def require(ok, description):
            if not ok: raise AssertionError(description)
        def control(auto_id): return window.child_window(auto_id=auto_id)
        def text(auto_id): return control(auto_id).window_text()
        def visible(auto_id): return control(auto_id).exists(timeout=0.1) and control(auto_id).is_visible()
        def wait_for(predicate,description,seconds=60):
            deadline=time.monotonic()+seconds
            while time.monotonic()<deadline:
                if process.poll() is not None: raise RuntimeError(f'Process exited during {description}: {process.returncode}')
                if predicate(): return
                time.sleep(0.2)
            raise TimeoutError(description)
        def check(name, assertion):
            assertion(); report['checks'].append({'name':name,'status':'PASS'})
            (output/'gui.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        def all_texts(): return [element.window_text() for element in window.descendants(control_type='Text')]
        def start():
            nonlocal process, window
            process=subprocess.Popen([str(Path(sys.argv[1]).resolve())])
            app=Application(backend='uia').connect(process=process.pid,timeout=30)
            window=app.window(title='Фотоархив');window.wait('visible ready',timeout=30)
            set_client(1440,900)
            return app
        def close():
            window.close();require(process.wait(timeout=15)==0,'Normal window close expected')
        def set_client(width,height):
            hwnd=window.handle
            outer=win32gui.GetWindowRect(hwnd); client=win32gui.GetClientRect(hwnd)
            offset=win32gui.ClientToScreen(hwnd,(0,0))
            dx=offset[0]-outer[0];dy=offset[1]-outer[1]
            nonwidth=outer[2]-outer[0]-client[2];nonheight=outer[3]-outer[1]-client[3]
            win32gui.MoveWindow(hwnd,-dx if width>=1920 else 20,-dy if height>=1080 else 20,width+nonwidth,height+nonheight,True)
            wait_for(lambda:win32gui.GetClientRect(hwnd)[2:]==(width,height),f'Client area {width}x{height}',10)
            time.sleep(0.3)
        def screenshot(name,description):
            win32api.SetCursorPos((0,0));time.sleep(0.4)
            hwnd=window.handle;client=win32gui.GetClientRect(hwnd);origin=win32gui.ClientToScreen(hwnd,(0,0))
            image=ImageGrab.grab(bbox=(origin[0],origin[1],origin[0]+client[2],origin[1]+client[3]))
            image.save(shots/(name+'.png'))
            report['screenshots'].append({'file':name+'.png','width':client[2],'height':client[3],'source':'native WPF client on Windows desktop','description':description})
            return image
        def assert_actions_fit():
            origin=win32gui.ClientToScreen(window.handle,(0,0)); client=win32gui.GetClientRect(window.handle)
            for name in ['AddFolderAction','ArchiveAction','SearchBox','ScanAction']:
                rect=control(name).rectangle()
                require(rect.left>=origin[0] and rect.right<=origin[0]+client[2]+1 and rect.top>=origin[1] and rect.bottom<=origin[1]+client[3]+1, name+' clipped')
        def choose_source(path):
            control('AddFolderAction').invoke()
            dialog=window.child_window(title='Выбери папку с фотографиями',control_type='Window');dialog.wait('visible ready',timeout=15)
            dialog.child_window(auto_id='1152',control_type='Edit').set_edit_text(str(path))
            dialog.child_window(auto_id='1',control_type='Button').invoke()
        def add_source(path,count):
            choose_source(path)
            wait_for(lambda:text('ScanAction')=='Обновить' and text('Status').startswith('Готово:') and rows('SELECT count(*) FROM photos')==[(count,)],'Successful source scan')
        def select_year(year):
            control('YearPicker').child_window(title=year,control_type='ListItem').select()
        def open_photo(name):
            control('SearchBox').set_edit_text(name)
            wait_for(lambda:'1 по фильтру' in text('Summary'),'Single searched photo')
            window.child_window(title=name,control_type='Button').invoke()
            wait_for(lambda:visible('DetailName'),'Right photo details')
        app=start()
        check('empty real catalog and zero metrics',lambda:require(text('MetricPhotos')=='0' and text('MetricSources')=='0' and text('PageCount')=='1 / 1','Empty catalog must not contain demonstration data'))
        empty=screenshot('01-empty-1440x900','Empty catalog; no imported or demo photos')
        check('reference shell colors',lambda:require(empty.getpixel((5,300))[:3]==(255,255,255) and empty.getpixel((500,5))[:3]==(255,255,255) and empty.getpixel((240,100))[:3]==(246,247,249),'White sidebar/topbar and #F6F7F9 canvas expected'))
        add_source(first,76);add_source(second,expected)
        check('two sources and recursive scan through native picker',lambda:require(rows('SELECT count(*) FROM sources')==[(2,)] and rows('SELECT count(*) FROM photos')==[(expected,)],'Real source and record counts'))
        check('real metric cards',lambda:require(text('MetricPhotos')==str(expected) and text('MetricDuplicates')=='2' and text('MetricPlaces')=='1' and text('MetricSources')=='2','Metrics must derive from scanned catalog'))
        check('month chronology',lambda:require('Июль 2024' in all_texts() and 'Август 2023' in all_texts(),'Actual EXIF months must group photos'))
        wait_for(lambda: len(control('PhotosGallery').descendants(control_type='Button'))>=10,'Photo tiles rendered')
        time.sleep(1)
        screenshot('02-photos-1440x900','Real catalog of deterministic artificial images; first page')
        window.capture_as_image().save(shots/'02-window-with-native-frame.png')
        control('SearchBox').set_edit_text('capture-2020')
        check('search by actual filename',lambda:require('1 по фильтру' in text('Summary'),'Search count'))
        control('SearchBox').set_edit_text('')
        select_year('2020');check('year filter',lambda:require('2 по фильтру' in text('Summary'),'Two genuine EXIF 2020 samples'))
        select_year('Все годы');check('first gallery page',lambda:require(text('PageCount')=='1 / 2','Bounded first page'))
        window.child_window(title='Следующая страница',control_type='Button').invoke()
        check('gallery next page',lambda:require(text('PageCount')=='2 / 2','Bounded second page'))
        window.child_window(title='Предыдущая страница',control_type='Button').invoke()
        open_photo('capture-2020.jpg')
        wait_for(lambda:any('1' in value and '200' in value for value in all_texts()) and 'EXIF' in all_texts(),'Real resolution and EXIF in details')
        check('photo opens right drawer rather than MessageBox',lambda:require(text('DetailName')=='capture-2020.jpg' and visible('DetailsClose') and 'EXIF' in all_texts(),'Real details drawer required'))
        rect=control('DetailMetadata').rectangle()
        check('detail metadata fits drawer width',lambda:require(all(child.rectangle().left>=rect.left and child.rectangle().right<=rect.right+1 for child in control('DetailMetadata').descendants(control_type='Text')),'Metadata exceeds drawer viewport'))
        screenshot('03-details-1440x900','Right drawer with real preview, metadata and source path')
        send_keys('{ESC}');wait_for(lambda:not visible('DetailName'),'Escape closes details')
        check('Escape closes photo drawer',lambda:require(not visible('DetailName'),'Drawer remains open'))
        control('SearchBox').set_edit_text('')
        control('NavPlaces').invoke()
        check('GPS list and honest map placeholder',lambda:require(text('PageTitle')=='Места съёмки' and 'Интерактивная карта появится позже' in all_texts() and len(control('PlacesList').wrapper_object().items())==1,'Only actual GPS records and no fake cities'))
        check('GPS privacy warning',lambda:require(any('OpenStreetMap' in value and 'переданы' in value for value in all_texts()),'Explicit coordinate transmission notice'))
        screenshot('04-map-1440x900','Honest map placeholder and actual GPS point; browser not opened')
        control('NavAlbums').invoke();check('albums remain honest empty state',lambda:require(text('PageTitle')=='Альбомы' and 'Альбомы появятся позже' in all_texts(),'No fictional albums'))
        screenshot('05-albums-1440x900','Albums unavailable without fictional content')
        control('NavSources').invoke();check('source cards show actual folders',lambda:require(text('PageTitle')=='Источники' and str(first) in all_texts() and str(second) in all_texts(),'Actual source paths'))
        screenshot('06-sources-1440x900','Two actual synthetic-test source folders')
        control('NavDuplicates').invoke();control('ExactDuplicatesFilter').invoke()
        compare=window.child_window(title_re='Сравнить: Точные копии.*',control_type='Button')
        wait_for(lambda:compare.exists(),'Exact duplicate card')
        screenshot('07-duplicates-1440x900','True SHA-256 duplicate group and real previews; future categories disabled')
        compare.invoke()
        expected_paths={os.path.normcase(str(first/'identical-a.bmp')),os.path.normcase(str(second/'identical-b.bmp'))}
        def members_match():
            members=control('DuplicateMembers').wrapper_object().items()
            observed={os.path.normcase(child.window_text()) for item in members for child in item.descendants(control_type='Text')}
            return len(members)==2 and observed==expected_paths
        wait_for(members_match,'Two exact expected duplicate paths')
        check('same-size different image excluded from exact duplicates',lambda:require(members_match(),'Duplicate content criterion'))
        screenshot('08-comparison-1440x900','All actual paths in the selected duplicate group')
        send_keys('{ESC}')
        control('ArchiveAction').set_focus();send_keys('{ENTER}')
        wait_for(lambda:visible('ArchiveTitle'),'Keyboard opens archive modal')
        check('archive operations and plan disabled',lambda:require(not control('ArchiveCopyMode').is_enabled() and not control('ArchiveMoveMode').is_enabled() and not control('ArchivePlanAction').is_enabled() and 'План ещё не рассчитан' in all_texts(),'No file operations or fake plan'))
        send_keys('{TAB}');check('modal blocks background keyboard actions',lambda:require(not control('AddFolderAction').is_enabled(),'Focus must remain in the modal'))
        screenshot('09-archive-1440x900','Archive placeholder; copying, moving and launch are disabled')
        send_keys('{ESC}');wait_for(lambda:not visible('ArchiveTitle'),'Escape closes archive')
        open_photo('unsupported.jpg')
        wait_for(lambda:'Превью недоступно' in text('DetailAvailability') or 'превью недоступно' in text('DetailAvailability'),'Unsupported image retained safely')
        check('unsupported image is retained without UI crash',lambda:require(text('DetailName')=='unsupported.jpg' and rows("SELECT count(*) FROM photos WHERE path LIKE '%unsupported.jpg'")==[(1,)],'Corrupt source must remain catalogued'))
        send_keys('{ESC}');control('SearchBox').set_edit_text('')
        for width,height,label in [(1366,768,'1366x768'),(1920,1080,'1920x1080'),(1093,614,'effective-125-percent'),(911,512,'effective-150-percent')]:
            set_client(width,height);control('NavPhotos').invoke();assert_actions_fit()
            screenshot('layout-'+label,'Native client '+str(width)+'x'+str(height)+('; effective workspace simulation, system DPI unchanged' if 'percent' in label else ''))
            control('ArchiveAction').invoke();wait_for(lambda:visible('ArchiveTitle'),'Archive at resized window')
            rect=control('ArchiveClose').rectangle();origin=win32gui.ClientToScreen(window.handle,(0,0));require(rect.right<=origin[0]+width and rect.bottom<=origin[1]+height,'Modal close clipped')
            send_keys('{ESC}');check('responsive layout '+label,lambda:None)
        set_client(1440,900);close();app=start()
        check('catalog persists across real restart',lambda:require(text('MetricPhotos')==str(expected) and f'{expected} в библиотеке' in text('Summary'),'Persisted actual catalog'))
        close()
        check('original names paths bytes creation and write times unchanged',lambda:require(before==snapshot() and {str(p.relative_to(root)) for p in root.rglob('*') if p.is_file()}==set(before),'Original source snapshot differs'))
        # Simulate absence using only the temporary directory created by this test.
        # This is NOT evidence of a physical drive disconnect.
        offline=root/'held-offline';second.rename(offline)
        try:
            app=start();control('NavSources').invoke();check('offline source entries preserved (simulation)',lambda:require(rows('SELECT count(*) FROM photos')==[(expected,)] and '○ Недоступен' in all_texts(),'Missing source was cleared or mislabelled'))
            control('ScanAction').invoke();wait_for(lambda:text('ScanAction')=='Обновить' and text('Status').startswith('Проверка неполная:'),'Offline scan warning')
            control('SearchBox').set_edit_text('identical-b');window.child_window(title='identical-b.bmp',control_type='Button').invoke()
            wait_for(lambda:'Источник недоступен' in text('DetailAvailability'),'Offline detail')
            screenshot('10-offline-detail-1440x900','Simulated missing temporary source; catalog entry preserved')
            send_keys('{ESC}');close()
        finally:
            if process is not None and process.poll() is None: process.kill();process.wait(timeout=10)
            offline.rename(second);offline=None
        check('original sources restored by test fixture without byte/time changes',lambda:require(before==snapshot(),'Temporary-source simulation changed image content or dates'))
        app=start()
        cancel_root=root/'cancel-source';cancel_root.mkdir()
        import io
        buffer=io.BytesIO();Image.new('RGB',(32,32),(9,19,29)).save(buffer,format='BMP');payload=buffer.getvalue()
        # Small files keep fixture memory bounded and create a deterministic cancellation window.
        for i in range(6000):(cancel_root/f'cancel-{i:05d}.bmp').write_bytes(payload)
        choose_source(cancel_root);wait_for(lambda:text('ScanAction')=='Остановить','Scan entered cancellable phase',10)
        control('ScanAction').invoke();wait_for(lambda:text('ScanAction')=='Обновить' and text('Status').startswith('Остановлено пользователем.'),'Cancellation must not report success')
        check('GUI cancellation is honest and responsive',lambda:require(control('AddFolderAction').is_enabled() and text('Status').startswith('Остановлено пользователем.'),'Cancellation state'))
        screenshot('11-cancelled-1440x900','Real GUI scan cancellation, no false success')
        close();check('initial image bytes and dates remain unchanged after all scenarios',lambda:require(before==snapshot(),'Original bytes/dates changed'))
        report['checks'].extend({'name':name,'status':'NOT RUN'} for name in ['physical drive disconnect','NTFS ACL denied','GPS browser launch','Windows 10 desktop','Windows 11 desktop','actual system DPI 125% and 150%'])
        report['status']='PASS';print('PASS: native WPF UI, screenshots and declared scenarios; remaining NOT RUN items explicit')
    except Exception:
        report['status']='FAIL' if sys.platform=='win32' else 'NOT RUN';report['error']=traceback.format_exc()
        if window is not None:
            try:window.capture_as_image().save(output/'failure.png');window.print_control_identifiers(filename=str(output/'control-tree.txt'))
            except Exception:pass
        raise
    finally:
        if process is not None and process.poll() is None:process.kill();process.wait(timeout=10)
        if offline is not None and offline.exists() and second is not None and not second.exists():offline.rename(second)
        if old_mode is not None:
            try:win32api.ChangeDisplaySettings(old_mode,0)
            except Exception:pass
        (output/'gui.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')


if __name__=='__main__':main()
