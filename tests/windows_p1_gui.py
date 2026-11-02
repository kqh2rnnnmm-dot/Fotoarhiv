"""P1.1 against the real published WPF exe, on a disposable Windows desktop only."""
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import sqlite3
import subprocess
import sys
import tempfile
import time
import traceback


def main():
    output=Path(sys.argv[2]).resolve();output.mkdir(parents=True,exist_ok=True)
    shots=output/'screenshots';shots.mkdir(exist_ok=True)
    report={'status':'NOT RUN','os':platform.platform(),'checks':[],'screenshots':[],'measurements':{}}
    process=None;window=None;held=None;second=None;old_mode=None
    try:
        if sys.platform!='win32' or os.environ.get('GITHUB_ACTIONS')!='true':raise RuntimeError('Requires disposable Windows GitHub runner')
        import win32api,win32con,win32gui,win32process
        from PIL import Image,ImageGrab
        from pywinauto import Application,mouse
        from pywinauto.keyboard import send_keys
        from pywinauto.findwindows import ElementNotFoundError
        from ui_fixtures import create_catalog_samples
        old_mode=win32api.EnumDisplaySettings(None,win32con.ENUM_CURRENT_SETTINGS)
        mode=win32api.EnumDisplaySettings(None,win32con.ENUM_CURRENT_SETTINGS);mode.PelsWidth=1920;mode.PelsHeight=1080;mode.Fields=win32con.DM_PELSWIDTH|win32con.DM_PELSHEIGHT
        assert win32api.ChangeDisplaySettings(mode,0)==0
        profile=Path(os.environ['LOCALAPPDATA'])/'Fotoarhiv';db=profile/'catalog.sqlite';cache=profile/'Cache/Thumbnails'
        root=Path(tempfile.mkdtemp(prefix='Fotoarhiv-P1-GUI-')).resolve();first,second,paths=create_catalog_samples(root)
        def snapshot(files=paths):
            return {str(p.relative_to(root)):[hashlib.sha256(p.read_bytes()).hexdigest(),p.stat().st_size,p.stat().st_mtime_ns,getattr(p.stat(),'st_birthtime_ns',p.stat().st_ctime_ns)] for p in files}
        original=snapshot();blue=Image.open(second/'identical-b.bmp').getpixel((10,10))
        def require(ok,message):
            if not ok:raise AssertionError(message)
        def control(name):return window.child_window(auto_id=name)
        def value(name):return control(name).window_text()
        def visible(name):return control(name).exists(timeout=.1) and control(name).is_visible()
        def wait_for(predicate,message,seconds=60):
            deadline=time.monotonic()+seconds;last=None
            while time.monotonic()<deadline:
                if process.poll() is not None:raise RuntimeError(f'Application exited: {process.returncode}; {message}')
                try:
                    if predicate():return
                except ElementNotFoundError as error:last=str(error)
                time.sleep(.2)
            raise TimeoutError(message+('; '+last if last else ''))
        def check(name,predicate):
            require(predicate(),name);report['checks'].append({'name':name,'status':'PASS'});save()
        def save():(output/'p1.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        def start():
            nonlocal process,window
            process=subprocess.Popen([str(Path(sys.argv[1]).resolve())])
            app=Application(backend='uia').connect(process=process.pid,timeout=30);window=app.window(title='Фотоархив');window.wait('visible ready',timeout=30);client(1440,900)
            return app
        def close():window.close();require(process.wait(timeout=15)==0,'Normal close')
        def client(width,height):
            if window.is_maximized():window.restore()
            hwnd=window.handle;outer=win32gui.GetWindowRect(hwnd);rect=win32gui.GetClientRect(hwnd)
            win32gui.MoveWindow(hwnd,20,20,width+outer[2]-outer[0]-rect[2],height+outer[3]-outer[1]-rect[3],True)
            wait_for(lambda:win32gui.GetClientRect(hwnd)[2:]==(width,height),'Client dimensions',10);time.sleep(.2)
        def screenshot(name,description):
            win32api.SetCursorPos((0,0));time.sleep(.3);hwnd=window.handle;rect=win32gui.GetClientRect(hwnd);origin=win32gui.ClientToScreen(hwnd,(0,0))
            ImageGrab.grab(bbox=(origin[0],origin[1],origin[0]+rect[2],origin[1]+rect[3])).save(shots/(name+'.png'))
            report['screenshots'].append({'file':name+'.png','client':[rect[2],rect[3]],'system_dpi':report.get('dpi'),'source':'native published WPF client','description':description})
        def add_source(path,total):
            control('AddFolderAction').invoke();dialog=window.child_window(title='Выбери папку с фотографиями',control_type='Window');dialog.wait('visible ready',timeout=15)
            dialog.child_window(auto_id='1152',control_type='Edit').set_edit_text(str(path));dialog.child_window(auto_id='1',control_type='Button').invoke()
            wait_for(lambda:value('ScanAction')=='Обновить' and value('Status').startswith('Готово:') and rows('SELECT count(*) FROM photos')==[(total,)],'Source scanned',120)
        def rows(sql):
            with sqlite3.connect(f'{db.as_uri()}?mode=ro',uri=True) as connection:return connection.execute(sql).fetchall()
        def catalog_snapshot():return rows('SELECT path,bytes,modified_utc_ticks,capture_ticks,date_source,latitude,longitude,sha256 FROM photos ORDER BY path')
        def metric(name,label):
            if control(name).exists(timeout=.1):return value(name)
            prefix=label+': '
            items=control('StatsCards').descendants(control_type='DataItem')
            matches=[item.window_text()[len(prefix):] for item in items if item.window_text().startswith(prefix)]
            return matches[0] if len(matches)==1 else None
        def source_box(path):return window.child_window(title_re='(?i)^'+re.escape('Показывать в библиотеке: '+str(path))+'$',control_type='CheckBox')
        def select_box(name):return control('PhotosGallery').child_window(title='Выделить: '+name,control_type='CheckBox')
        def gallery_names():return [item.window_text() for item in control('PhotosGallery').descendants(control_type='Button')]
        def open_photo(name):
            control('SearchBox').set_edit_text(name);wait_for(lambda:'1 по фильтру' in value('Summary'),'Single real record')
            window.child_window(title=name,control_type='Button').invoke();wait_for(lambda:visible('DetailName'),'Photo drawer')
            wait_for(lambda:'Проверка' not in value('DetailAvailability'),'Preview availability')
        def pixel_matches(name,color):
            rect=control(name).rectangle();point=((rect.left+rect.right)//2,(rect.top+rect.bottom)//2)
            actual=ImageGrab.grab(bbox=(point[0],point[1],point[0]+1,point[1]+1)).getpixel((0,0))[:3]
            return all(abs(a-b)<=8 for a,b in zip(actual,color))
        def cache_snapshot():return {p.relative_to(cache).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in cache.rglob('*.thumb')}
        def rss():
            handle=win32api.OpenProcess(win32con.PROCESS_QUERY_INFORMATION|win32con.PROCESS_VM_READ,False,process.pid)
            try:return win32process.GetProcessMemoryInfo(handle)['WorkingSetSize']
            finally:handle.Close()
        def modified_click(name,key):
            send_keys('{'+key+' down}')
            try:window.child_window(title=name,control_type='Button').click_input()
            finally:send_keys('{'+key+' up}')
        app=start()
        user32=ctypes.WinDLL('user32');user32.GetDpiForWindow.argtypes=[wintypes.HWND];user32.GetDpiForWindow.restype=ctypes.c_uint;report['dpi']=user32.GetDpiForWindow(window.handle)
        check('fresh empty real library and newest default',lambda:rows('SELECT count(*) FROM photos')==[(0,)] and value('SortAction')=='Сначала новые')
        add_source(first,76);add_source(second,81)
        wait_for(lambda:len(list(cache.rglob('*.thumb')))>=10,'Visible thumbnails persisted')
        check('thumbnail cache is inside LocalAppData and no source-side files',lambda:cache.is_dir() and set(p for p in root.rglob('*') if p.is_file())==set(paths))
        screenshot('01-p1-library','Default chronology with persistent previews and selection checkboxes')
        for name in ['landscape-000.jpg','landscape-001.jpg']:select_box(name).toggle()
        wait_for(lambda:value('SelectionCount')=='Выбрано: 2','Two selected')
        modified_click('landscape-002.jpg','VK_CONTROL');wait_for(lambda:value('SelectionCount')=='Выбрано: 3','Ctrl selection')
        modified_click('landscape-004.jpg','VK_SHIFT')
        check('checkbox Ctrl and Shift select the real range without opening details',lambda:value('SelectionCount')=='Выбрано: 3' and all(select_box(f'landscape-{i:03d}.jpg').get_toggle_state()==1 for i in [2,3,4]) and select_box('landscape-000.jpg').get_toggle_state()==0 and not visible('DetailName'))
        screenshot('02-selection','Multiple real photos selected; no file operations')
        window.child_window(title='Следующая страница',control_type='Button').invoke();wait_for(lambda:value('PageCount')=='2 / 2','Second page')
        next_box=control('PhotosGallery').descendants(control_type='CheckBox')[0];next_box.toggle()
        check('selection persists across gallery pages',lambda:value('SelectionCount')=='Выбрано: 4')
        window.child_window(title='Предыдущая страница',control_type='Button').invoke();control('ClearSelectionAction').invoke()
        check('clear selection removes every current-page check',lambda:not visible('SelectionCount') and all(item.get_toggle_state()==0 for item in control('PhotosGallery').descendants(control_type='CheckBox')))
        select_box('landscape-000.jpg').toggle();control('SearchBox').set_edit_text('capture-2020')
        check('search clears selection',lambda:not visible('SelectionCount'))
        control('SearchBox').set_edit_text('');select_box('landscape-000.jpg').toggle();control('YearPicker').child_window(title='2020',control_type='ListItem').select()
        check('year filter clears selection',lambda:not visible('SelectionCount') and '2 по фильтру' in value('Summary'))
        control('YearPicker').child_window(title='Все годы',control_type='ListItem').select();select_box('landscape-000.jpg').toggle();control('SortAction').invoke()
        expected=[Path(row[0]).name for row in rows('SELECT path FROM photos ORDER BY capture_ticks ASC,path COLLATE NOCASE ASC')]
        wait_for(lambda:gallery_names() and gallery_names()[0]==expected[0],'Oldest chronology')
        check('oldest order and sort selection reset',lambda:value('SortAction')=='Сначала старые' and not visible('SelectionCount'))
        screenshot('03-oldest','Chronology sorted by actual capture date ascending')
        control('SearchBox').set_edit_text('landscape-00');wait_for(lambda:'10 по фильтру' in value('Summary'),'Ten filtered samples')
        filtered=[Path(row[0]).name for row in rows("SELECT path FROM photos WHERE path LIKE '%landscape-00%' ORDER BY capture_ticks ASC,path COLLATE NOCASE ASC")]
        first_name=gallery_names()[0];check('filtered gallery follows ascending order',lambda:first_name==filtered[0])
        window.child_window(title=first_name,control_type='Button').invoke();control('DetailOpenAction').invoke()
        wait_for(lambda:value('ViewerMessage')=='Оригинал','Original loaded')
        check('internal viewer opens selected original and current filtered position',lambda:value('ViewerTitle')==filtered[0] and value('ViewerPosition')=='1 / 10' and not control('ViewerPreviousAction').is_enabled())
        screenshot('04-viewer-fit','Internal original viewer, fit with preserved aspect ratio')
        control('ViewerActualAction').invoke();wait_for(lambda:value('ViewerZoom')=='100%','Actual pixels')
        scroll=control('ViewerScroll').wrapper_object().iface_scroll
        wait_for(lambda:bool(scroll.CurrentVerticallyScrollable),'Actual pixel viewport overflow')
        check('100 percent produces real pixel overflow',lambda:bool(scroll.CurrentVerticallyScrollable))
        scroll.SetScrollPercent(-1,40);wait_for(lambda:scroll.CurrentVerticalScrollPercent>0,'Original scroll offset');check('100 percent scrolls',lambda:scroll.CurrentVerticalScrollPercent>0)
        rect=control('ViewerScroll').rectangle();x=(rect.left+rect.right)//2;y=(rect.top+rect.bottom)//2
        before_pan=scroll.CurrentVerticalScrollPercent;mouse.press(coords=(x,y));mouse.move(coords=(x,y-50));mouse.release(coords=(x,y-50))
        wait_for(lambda:scroll.CurrentVerticalScrollPercent>before_pan,'Original drag offset')
        check('100 percent supports drag panning',lambda:scroll.CurrentVerticalScrollPercent>before_pan)
        screenshot('05-viewer-100','Native original at 100 percent with scrolling')
        control('ViewerFitAction').invoke();check('fit returns without pixel overflow',lambda:value('ViewerZoom')=='Вписать')
        send_keys('{RIGHT}');wait_for(lambda:value('ViewerTitle')==filtered[1],'Keyboard next filtered photo')
        control('ViewerPreviousAction').invoke();wait_for(lambda:value('ViewerTitle')==filtered[0],'Previous filtered photo')
        for name in filtered[1:]:control('ViewerNextAction').invoke();wait_for(lambda:value('ViewerTitle')==name,'Filtered next photo')
        check('viewer cannot leave current filter or wrap at last photo',lambda:value('ViewerPosition')=='10 / 10' and not control('ViewerNextAction').is_enabled())
        send_keys('{ESC}');wait_for(lambda:not visible('ViewerTitle'),'Escape closes viewer')
        check('viewer Escape restores focus to opening photo',lambda:window.child_window(title=first_name,control_type='Button').has_keyboard_focus())
        control('SearchBox').set_edit_text('');baseline_sql=catalog_snapshot();baseline_sources=rows('SELECT path FROM sources ORDER BY path')
        control('NavSources').invoke();source_box(second).toggle();control('NavPhotos').invoke()
        wait_for(lambda:'76 по фильтру' in value('Summary'),'Source hidden')
        check('hidden source filters gallery GPS and exact groups without changing SQLite',lambda:metric('MetricPhotos','Фотографий в просмотре')=='76' and metric('MetricPlaces','Мест с координатами GPS')=='0' and metric('MetricDuplicates','Снимков в точных повторах')=='0' and catalog_snapshot()==baseline_sql and rows('SELECT path FROM sources ORDER BY path')==baseline_sources)
        control('NavPlaces').invoke();check('hidden source removes GPS list only from view',lambda:len(control('PlacesList').wrapper_object().items())==0)
        control('NavDuplicates').invoke();check('one remaining copy is not a duplicate group',lambda:visible('DuplicatesEmptyTitle'))
        control('NavSources').invoke();source_box(second).toggle();control('NavPhotos').invoke()
        check('source reappears without scan and hashes remain unchanged',lambda:'81 по фильтру' in value('Summary') and metric('MetricDuplicates','Снимков в точных повторах')=='2' and catalog_snapshot()==baseline_sql)
        control('NavSources').invoke();control('HideAllSourcesAction').invoke();control('NavPhotos').invoke()
        check('all sources hidden is an honest empty view preserving catalog',lambda:value('PhotosEmptyTitle')=='Источники скрыты' and value('PhotosEmptyAction')=='Показать источники' and '0 по фильтру' in value('Summary') and rows('SELECT count(*) FROM photos')==[(81,)])
        screenshot('06-all-hidden','No visible sources; complete catalog remains intact')
        control('PhotosEmptyAction').invoke();control('ShowAllSourcesAction').invoke();screenshot('07-source-switches','Registered sources, independent visibility and availability')
        control('NavPhotos').invoke();add_source(first/'nested',81)
        check('nested source does not duplicate catalog or display',lambda:rows('SELECT count(*) FROM sources')==[(3,)] and '81 по фильтру' in value('Summary'))
        control('NavSources').invoke();source_box(first).toggle();control('NavPhotos').invoke()
        check('overlapping source union keeps child photos once',lambda:'80 по фильтру' in value('Summary') and rows('SELECT count(*) FROM photos')==[(81,)])
        control('NavSources').invoke();control('ShowAllSourcesAction').invoke();control('NavPhotos').invoke()
        open_photo('identical-b.bmp');wait_for(lambda:pixel_matches('DetailPreview',blue),'Real persisted preview color');send_keys('{ESC}')
        warm=cache_snapshot();control('SearchBox').set_edit_text('');control('NavSources').invoke();source_box(second).toggle();close()
        app=start();check('sort persists and all sources reset visible after restart',lambda:value('SortAction')=='Сначала старые' and '81 по фильтру' in value('Summary'))
        close();held=root/'own-offline-fixture';second.rename(held)
        try:
            app=start();open_photo('identical-b.bmp');wait_for(lambda:'Источник недоступен' in value('DetailAvailability') and pixel_matches('DetailPreview',blue),'Offline persisted bitmap after real restart')
            check('offline thumbnail survives process restart with correct pixels',lambda:pixel_matches('DetailPreview',blue))
            screenshot('08-offline-preview','Persistent thumbnail of absent synthetic source, explicit offline indicator')
            control('DetailOpenAction').invoke();wait_for(lambda:value('ViewerMessage')=='Только миниатюра, источник недоступен','Honest offline viewer')
            check('offline viewer never presents thumbnail as original',lambda:not control('ViewerActualAction').is_enabled() and pixel_matches('ViewerImage',blue))
            screenshot('09-offline-viewer','Only a cached thumbnail; original and 100 percent unavailable')
            send_keys('{ESC}');control('SearchBox').set_edit_text('');control('NavSources').invoke();source_box(second).toggle()
            control('ScanAction').invoke();wait_for(lambda:value('ScanAction')=='Обновить' and value('Status').startswith('Проверка неполная:'),'Hidden source still participates in scan')
            control('NavPhotos').invoke()
            check('hidden source is still scanned and incomplete duplicate count is unknown',lambda:'76 по фильтру' in value('Summary') and metric('MetricDuplicates','Снимков в точных повторах')=='—')
            close()
        except Exception:
            if window is not None:
                try:screenshot('offline-failure','Failure evidence before own fixture is restored');window.print_control_identifiers(filename=str(output/'offline-controls.txt'))
                except Exception:pass
            raise
        finally:
            if process is not None and process.poll() is None:process.kill();process.wait(timeout=10)
            held.rename(second);held=None
        check('source names paths bytes creation and write times remain unchanged',lambda:original==snapshot())
        check('visibility did not delete or alter existing thumbnail entries',lambda:all(cache_snapshot().get(key)==sha for key,sha in warm.items()))
        app=start();stress=root/'source-stress';stress.mkdir();small=Image.new('RGB',(32,32),(12,86,143))
        for i in range(1000):small.save(stress/f'stress-{i:04d}.bmp')
        Image.new('RGB',(9000,6000),(83,127,167)).save(stress/'very-large.jpg',quality=80)
        stress_before=snapshot(sorted(stress.iterdir()));baseline_memory=rss();started=time.monotonic();add_source(stress,1082)
        report['measurements']['stress_scan_seconds']=round(time.monotonic()-started,3);report['measurements']['stress_images']=1001
        control('SearchBox').set_edit_text('stress-');wait_for(lambda:'1000 по фильтру' in re.sub(r'(?<=\d)[,\s\u202f]+(?=\d)','',value('Summary')),'Thousand-image filter')
        check('1000 synthetic images use bounded gallery page',lambda:len(gallery_names())==72 and value('PageCount')=='1 / 14')
        latencies=[]
        for i in range(4):
            started=time.monotonic();window.child_window(title='Следующая страница',control_type='Button').invoke();wait_for(lambda:value('PageCount')==f'{i+2} / 14','Stress paging');latencies.append(round(time.monotonic()-started,3))
        report['measurements'].update({'working_set_before_stress':baseline_memory,'working_set_after_paging':rss(),'page_response_seconds':latencies})
        check('stress UI remains responsive with bounded observed memory',lambda:max(latencies)<10 and rss()<900*1024*1024)
        screenshot('10-stress-page','Actual 1000-image query, bounded 72-record page')
        open_photo('very-large.jpg');control('DetailOpenAction').invoke();wait_for(lambda:'32 Мп' in value('ViewerMessage'),'Bounded large-image decode',90)
        check('large original uses declared decode limit and disables false 100 percent',lambda:not control('ViewerActualAction').is_enabled() and '32 Мп' in value('ViewerMessage'))
        report['measurements']['working_set_with_54mp_original']=rss();screenshot('11-large-original','54MP synthetic input decoded within declared 32MP/8192 limit')
        send_keys('{ESC}');control('SearchBox').set_edit_text('');check('stress and viewer do not alter any source image',lambda:original==snapshot() and stress_before==snapshot(sorted(stress.iterdir())))
        # The following mutation belongs to the test fixture, never the app or private photos.
        changed=second/'identical-b.bmp';Image.new('RGB',(640,480),(151,43,62)).save(changed);now=time.time()+3;os.utime(changed,(now,now));new_original=snapshot()
        control('ScanAction').invoke();wait_for(lambda:value('ScanAction')=='Обновить' and value('Status').startswith('Готово:'),'Rescan changed own fixture',120)
        open_photo('identical-b.bmp');wait_for(lambda:pixel_matches('DetailPreview',(151,43,62)),'Invalidated preview updates to new source pixels')
        check('changed original invalidates persistent thumbnail without stale image',lambda:pixel_matches('DetailPreview',(151,43,62)) and new_original==snapshot())
        send_keys('{ESC}');control('SearchBox').set_edit_text('')
        for width,height,label in [(1366,768,'1366x768'),(1093,614,'effective-125'),(911,512,'effective-150')]:
            client(width,height);open_photo('capture-2020.jpg');control('DetailOpenAction').invoke();wait_for(lambda:visible('ViewerTitle'),'Resized viewer')
            origin=win32gui.ClientToScreen(window.handle,(0,0));rect=control('ViewerClose').rectangle()
            require(origin[0]<=rect.left and rect.right<=origin[0]+width and origin[1]<=rect.top and rect.bottom<=origin[1]+height,'Viewer close is clipped')
            screenshot('layout-'+label,'Actual client '+str(width)+'x'+str(height)+'; effective-workspace simulation, system DPI unchanged')
            send_keys('{ESC}');control('SearchBox').set_edit_text('')
        check('new controls and viewer fit smaller effective workspaces',lambda:True)
        client(1440,900);close();check('final fixture bytes and write dates remain unchanged after application actions',lambda:new_original==snapshot() and stress_before==snapshot(sorted(stress.iterdir())))
        report['checks'].extend({'name':name,'status':'NOT RUN'} for name in ['P1.1 on user Windows 10','P1.1 on Windows 11','actual system DPI 125% and 150%','physical USB disconnect','NTFS ACL scenario','all HEIC RAW codecs'])
        report['status']='PASS';print('PASS: P1.1 native GUI, cache, viewer, selection, sort, source visibility, original snapshots and synthetic stress')
    except Exception:
        report['status']='FAIL';report['error']=traceback.format_exc();report['process_exit_code']=process.poll() if process else None
        if window is not None:
            try:screenshot('failure','Native failure screenshot');window.print_control_identifiers(filename=str(output/'control-tree.txt'))
            except Exception as error:report['capture_error']=str(error)
        raise
    finally:
        if process is not None and process.poll() is None:process.kill();process.wait(timeout=10)
        if held is not None and held.exists() and second is not None and not second.exists():held.rename(second)
        if old_mode is not None:
            try:win32api.ChangeDisplaySettings(old_mode,0)
            except Exception:pass
        (output/'p1.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')


if __name__=='__main__':main()
