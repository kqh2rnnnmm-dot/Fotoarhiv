# Фотоархив 0.2.1 — отчёт по CODEX_TASK_002

Дата: 01.11.2026 (Москва). **P0 ОТКРЫТ. Проверенной Windows-сборки пока нет.**
Новые функции не добавлены. Все файлы src/ и исходных xUnit-проектов совпадают с переданным
проектом по SHA-256; номер приложения оставлен **0.2.1**. Изменены только CI, проверочные
инструменты, документация; добавлен искусственный набор для ручной проверки.

## Доступ и причина незавершённости

Доступна Linux x64 среда Debian 13, SDK 10.0.403/runtime 10.0.14. Windows-машина,
Windows desktop и инструменты управления Windows runner в этой сессии отсутствуют.
Первый поиск по GitHub-инсталляции и список доступных репозиториев вернули пустой результат.
После уточнения пользователя поиск по владельцу обнаружил публичный репозиторий
https://github.com/kqh2rnnnmm-dot/Fotoarhiv . Повторное чтение main подтвердило только
README.md (11 байт), а API Actions — 0 запусков. Исходников и .github/workflows в main нет.
Следовательно, репозиторий создан, но Windows workflow пока не загружен и не выполнялся.
Прямой запрос коллекции actions/workflows отклонён ограничением GitHub fetch (400);
вывод об отсутствии workflow в main основан на чтении корня, а не этом отклонённом запросе.
Сетевые ограничения не являются причиной отсутствия Windows. GitHub используется только
для чтения; push, PR, workflow dispatch и публикация не выполнялись.
Доказательство повторной проверки — verification/task002/github-readback.json.

**Actions run ID/URL: отсутствует; Windows run не состоялся.** Подготовленный workflow
невозможно назвать проверенным выполнением. Фактическую ОС будущего runner он запишет
в TestResults/run.json; выбран windows-2025 (Windows Server, не Windows 11).

## Фактически выполненные проверки Task 002

Все пути без префикса находятся в `verification/task002/`.

| Проверка | Статус | Среда | Доказательство |
|---|---|---|---|
| Целостность входного ZIP, полный состав проекта, 18 Fact-тестов | PASS | Linux/Python | input-manifest.json: SHA-256 входного ZIP и всех 42 файлов |
| Restore | PASS | Linux/.NET 10 | restore.log |
| Release всего solution, включая WPF с Windows targeting | PASS: 0 ошибок, 0 предупреждений | Linux/.NET 10 | build.log |
| xUnit | PASS: 18, FAIL: 0, SKIP: 0 | Linux/.NET 10 | tests.log, linux-tests.trx |
| Контроль CI: ошибка команды, запрет ZIP без проверок/зависимостей, полнота ZIP | PASS: 4 | Linux/Python, искусственные файлы | ci-controls-tests.log; это не тесты Windows-приложения |
| Неизменность src/ и исходных проектов xUnit | PASS: 13 файлов | Linux/SHA-256 | application-unchanged.json |
| Python/XML/YAML и настройка runner/выгрузки evidence | PASS статически | Linux | static-checks.log; не выполнение Actions |
| Отказ CI/GUI-скриптов имитировать Windows на Linux | PASS защиты, Windows NOT RUN | Linux | ci-platform-guard.log/json, gui-platform-guard.log, gui-platform-guard/gui.json |
| Искусственный EXIF JPEG и ручной набор | PASS проверка файлов/метаданных | Linux/Pillow | manual-samples-check.log; не GUI-тест |
| Windows restore/Release/xUnit/publish | NOT RUN | Windows недоступна | нет Windows-логов и CI run |
| Запуск, SQLite и закрытие exe на Windows | NOT RUN | Windows недоступна | windows_smoke.py сохранён, выполнения нет |
| UI Automation источников/поиска/года/страниц/дублей | NOT RUN | Windows недоступна | windows_gui.py подготовлен; не выдаётся за выполненный |
| Ручные сценарии на Windows 11 | NOT RUN | Windows 11 недоступна | WINDOWS_CHECKLIST_RU.md |
| Физическое отключение диска | NOT RUN | нет тестового диска/Windows | xUnit недоступного пути не заменяет эту проверку |
| Проверка на ПК пользователя | NOT RUN | нет результата пользователя | отдельный этап приёмки |

Старые логи `verification/01…14` и `verification/linux-tests.trx` относятся к Task 001.
Исходный отчёт сохранён в `verification/BUILD_REPORT_TASK001_RU.md`. Его успешная
кросс-публикация win-x64 не переносится в графу «Windows PASS». В Task 002 повторная
кросс-публикация не выполнялась: приложение идентично, а она не решает задачу запуска Windows.

## Выполненные команды

Рабочая папка `/workspace/Fotoarhiv_Task002`; реальный SDK `/tmp/fotoarhiv-dotnet/dotnet`.

```text
dotnet --info
dotnet restore Fotoarhiv.slnx -p:EnableWindowsTargeting=true
dotnet build Fotoarhiv.slnx -c Release --no-restore -p:EnableWindowsTargeting=true
dotnet test tests/Fotoarhiv.Tests/Fotoarhiv.Tests.csproj -c Release --no-build --logger "trx;LogFileName=linux-tests.trx" --results-directory verification/task002
python3 -m unittest discover -s tests -p test_ci_evidence.py -v
```

Дополнительно выполнены разбор Python AST, XML/YAML, SHA-256-сравнение с входным ZIP,
`git diff --check`, проверка искусственного EXIF и CRC результирующих архивов.
Проверочные запуски windows_ci.py/windows_gui.py на Linux завершились ненулевым кодом
и статусом NOT RUN; приложение при этом не запускалось. Это проверка защиты от ложного успеха.
API pywinauto 0.6.9 проверен по исходникам его официального пакета; это не проверка UIA на Windows.

## Что подготовлено для настоящего Windows run

`.github/workflows/build-windows.yml` запускает CI-only `tests/windows_ci.py` на windows-2025.
Последовательность: SDK → закреплённые Python-зависимости → 4 теста контроля evidence →
restore → Release build → 18+ xUnit с TRX → self-contained win-x64 publish → существующий
startup smoke → UI Automation → ZIP полного publish. Сбой любого обязательного шага
прерывает упаковку. В TRX требуется не менее 18 пройденных тестов и отсутствие пропусков.
Нет скрытого перевода ошибки в успех или отключения тестов.

`tests/windows_gui.py` через pywinauto/UIA должен проверять реальный интерфейс:
пустую галерею; стандартный диалог добавления двух папок; вложенную папку; 79 искусственных
изображений; EXIF/файловую дату в каталоге; поиск; фильтр года; страницы; карточку EXIF;
четыре раздела; состав группы точных дублей; повторный запуск; неизменность имён,
расположения, SHA-256, времени записи/создания. Каталог не предзаполняется SQL: фото
попадают в него через выбор папок и сканирование приложения. Запросы к SQLite только read-only.

Windows GUI-сценарий **НЕ проверяет** физический диск, отмену через GUI, NTFS ACL,
GPS/открытие браузера и офлайн-источник в GUI. Эти пункты записываются NOT RUN даже при
успехе реализованного поднабора; их нужно дополнить по чек-листу. UIA-селекторы и работа
на desktop runner остаются неподтверждёнными до первого реального запуска. При проблеме
селекторов нужно исправлять тест/блокирующий дефект по журналу, а не отключать проверки.

Будущий артефакт **Fotoarhiv-Windows-test-results** содержит:
- `run.json`: фактическая ОС, версия Windows, commit, URL run, команды, UTC-время, коды возврата;
- отдельные логи SDK/restore/build/xUnit/publish/startup/gui и `windows-tests.trx`;
- `gui/gui.json`, снимки окна; при сбое — доступный снимок и дерево элементов;
- `publish-manifest.json` со всеми файлами/размерами/SHA-256 и `archive.json` с SHA-256 ZIP.

Выгрузка evidence настроена с `if: always()`. При сбое установки SDK/Python до начала
сценария отдельных TestResults может не быть; причина останется в журнале соответствующего
шага Actions. Публикация папкой сохраняет все runtime/SQLite DLL, single-file не используется.
Python-инструменты и их зависимости в дистрибутив приложения не входят.
Скрипты работают только на disposable GitHub Windows runner и отказываются использовать
уже существующий каталог: личные каталоги не очищаются и не перезаписываются.

## Перечень изменений

- `AGENTS.md`, `INSTRUCTIONS_CHATGPT_PROJECT.md`: исправлен устаревший статус v0.2,
  указаны подтверждения Linux и открытый Task 002.
- `.github/workflows/build-windows.yml`: фиксированная линия Windows Server runner,
  проверочный сценарий, UIA-зависимости, полный сбор evidence.
- `tests/windows_ci.py`: команды, контроль TRX, журналирование, упаковка после успеха,
  манифест полного publish и защита существующей БД.
- `tests/windows_gui.py`: подготовленный сценарий реальных действий UIA, снимки/результаты,
  явные NOT RUN для непокрытых сценариев. Это не пользовательский запускатель.
- `tests/test_ci_evidence.py`: 4 теста механизмов evidence/упаковки.
- `tests/requirements-windows.txt`: только CI-зависимости pywinauto 0.6.9/Pillow 11.3.0.
- `tests/manual_samples/`: 80 маленьких искусственных изображений, описание и SHA-256.
  Два источника, 76 BMP в первом, 4 файла во втором; это отдельный ручной набор,
  а не результат GUI-прогона. TIFF содержит искусственный GPS.
- `README_RU.md`, `WINDOWS_CHECKLIST_RU.md`, `BUILD_REPORT_RU.md`: актуальный статус,
  инструкция без CLI и границы подтверждения.
- `.gitignore`: исключены Python-кеши; `verification/task002/`: новые логи/манифесты;
  старый отчёт Task 001 сохранён отдельно. Исходные tasks/ и REVIEW_021_RU.md сохранены.

Код, XAML, версия и зависимости приложения, исходные C#-тесты не изменены.
Новые продуктовые функции, операции с оригиналами, VBS/PS1/CMD-запускатели не добавлялись.
Ни личные фото, ни личные GPS не использовались. Создавались только искусственные изображения.

## Ограничения и результат

Сохранены ограничения v0.2.1: нет офлайн-кеша миниатюр; до 72 превью декодируются
синхронно; кеш SHA-256 проверяется по размеру/времени; изменение с сохранением обоих
признаков не распознаётся. ОС может обновлять время доступа при чтении. Полнота RAW/HEIC
не проверена. EXIF без часового пояса не превращается в придуманное UTC.
«Собрать архив» остаётся заглушкой. Цифровая подпись/SmartScreen/антивирус не проверены,
защита не отключалась и обход не предлагается. Проверка NTFS и настоящего отключения
диска остаётся отдельной от Linux-тестов. Предположения об отзывчивости GUI не измерялись.

Доставка:
- `Fotoarhiv_SOURCE_v0_2_1_Task002.zip`: полный проект с исходными и новыми доказательствами;
- `Fotoarhiv_TEST_EVIDENCE_Task002.zip`: новые доказательства и отчёт отдельно;
- `BUILD_REPORT_RU.md`, `WINDOWS_CHECKLIST_RU.md`: отдельно и в проекте;
- `Fotoarhiv_Windows_x64_TEST.zip`: **НЕ СОЗДАН**, нет подтверждённых Windows build/startup.

**Написано:** CI/GUI-проверки и документация. **Скомпилировано:** на Linux, WPF targeting.
**Протестировано на Windows CI:** NOT RUN. **Вручную на Windows:** NOT RUN.
**На компьютере пользователя:** NOT RUN. **P0:** OPEN.

Следующий конкретный шаг: владелец переносит полный проект в kqh2rnnnmm-dot/Fotoarhiv через GitHub Desktop,
запускает Actions, возвращает артефакт evidence и ZIP (если проверки успешны).
Пошаговая проверка двойным щелчком без терминала — в WINDOWS_CHECKLIST_RU.md.
