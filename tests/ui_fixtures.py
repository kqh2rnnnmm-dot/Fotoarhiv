"""Deterministic synthetic files for GUI tests only; never imported by the application."""
from pathlib import Path
from PIL import Image, ImageDraw
import os


def create_catalog_samples(root):
    root = Path(root).resolve()
    first, second = root / 'source-a', root / 'source-b'
    (first / 'nested').mkdir(parents=True); second.mkdir()
    for i in range(75):
        image = Image.new('RGB', (1200, 900))
        draw = ImageDraw.Draw(image)
        colors = [(29, 107, 161), (92, 129, 144), (180, 129, 97), (91, 111, 89), (87, 102, 147)]
        r, g, b = colors[i % len(colors)]
        for y in range(900):
            t = y / 900
            draw.line([(0, y), (1200, y)], fill=(int(r + (220-r)*t), int(g+(220-g)*t), int(b+(210-b)*t)))
        draw.ellipse((780-i*3%200, 100, 930-i*3%200, 250), fill=(248, 226, 177))
        draw.polygon([(0, 640), (250+i*2, 280+i*3), (580, 650), (830, 350+i*2), (1200, 620), (1200, 900), (0, 900)], fill=(r//2, g//2+25, b//2+10))
        draw.polygon([(0, 740), (1200, 650), (1200, 900), (0, 900)], fill=(r//2+20, g//2+35, b//2+40))
        for x in range(0, 1200, 75):
            draw.line([(x, 820+i%35), (x+40, 820+i%35)], fill=(130, 166, 176), width=2)
        date = f'2024:07:{18-i:02d}' if i < 10 else f'2023:08:{22-(i-10):02d}' if i < 13 else f'2022:05:{i%28+1:02d}'
        exif = Image.Exif(); exif[0x8769] = {0x9003: date + ' 09:10:11'}
        image.save(first / 'nested' / f'landscape-{i:03d}.jpg', exif=exif, quality=88)
    Image.new('RGB', (640,480), (39,99,151)).save(first / 'identical-a.bmp')
    (second/'identical-b.bmp').write_bytes((first/'identical-a.bmp').read_bytes())
    Image.new('RGB',(640,480),(40,99,151)).save(second/'same-size-different.bmp')
    exif = Image.Exif(); exif[0x8769] = {0x9003:'2020:07:08 09:10:11'}
    Image.new('RGB',(1200,900),(179,128,95)).save(second/'capture-2020.jpg',exif=exif)
    gps = Path(__file__).parent/'manual_samples/Source_B/synthetic-gps.tiff'
    (second/'synthetic-gps.tiff').write_bytes(gps.read_bytes())
    (second/'unsupported.jpg').write_bytes(b'Not an image: artificial corrupt-file fixture')
    photos = sorted(p for p in root.rglob('*') if p.is_file())
    for p in photos: os.utime(p,(1704110400,1704110400))
    return first, second, photos
