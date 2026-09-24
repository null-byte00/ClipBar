# ClipBar — архитектура

Лёгкая программа записи экрана для Windows 11 в стиле Xbox Game Bar:
**мгновенный откат** (последние 15 сек … 60 мин), обычная запись, скриншоты,
нормальный звук (система + микрофон отдельными дорожками), редактор клипов,
свои горячие клавиши, оверлей как у Game Bar.

Стек: **C# / .NET 10 / WPF** + **WPF-UI 4.3** (Fluent, Mica) + **NAudio 3.1** (WASAPI)
+ **H.NotifyIcon.Wpf** (трей) + **FFmpeg 9** (`tools\ffmpeg.exe`).

Открыть в Visual Studio: `ClipBar.sln`. Сборка: `dotnet build src\ClipBar`.

---

## Как это работает (и почему не грузит ПК)

```
 ┌──────────── ffmpeg.exe (1 процесс, AboveNormal) ─────────────┐
 │ ddagrab (Desktop Duplication, весь экран, GPU-текстура)       │
 │   → hwmap=qsv → h264_qsv (кодирует видеокарта Intel Arc)      │
 │ \\.\pipe\clipbar_sys_* (f32le 48k stereo) ─┐                  │
 │ \\.\pipe\clipbar_mic_* (f32le 48k stereo) ─┴→ amix → AAC ×3   │
 │   → -f segment, 1-секундные .ts  →  %LocalAppData%\ClipBar\buffer
 └───────────────────────────────────────────────────────────────┘
        ▲ PCM в реальном времени
 ┌──────┴─────── ClipBar.exe ────────────────────────────────────┐
 │ AudioHub: WASAPI loopback (звук ПК) + WASAPI mic → 48k/stereo  │
 │           громкость/mute → named pipes (тишина, если звука нет)│
 │ CaptureEngine: держит N последних сегментов, удаляет старые    │
 │   «Сохранить откат» = concat последних N сегментов -c copy     │
 │   (без перекодирования → 5 минут сохраняются за 1–2 сек)       │
 │   «Запись» = перестать удалять сегменты с момента старта,      │
 │   на стопе склеить → нулевая доп. нагрузка                     │
 └───────────────────────────────────────────────────────────────┘
```

Проверено на этом ПК (Intel Arc A380, Ryzen 5 5600G): захват 1080p60 через
QSV = **~1.4 % CPU**, склейка 9 сек из сегментов = **0.2 сек**.

Проверенная команда захвата:
```
ffmpeg -filter_complex "ddagrab=output_idx=0:framerate=60:draw_mouse=1,hwmap=derive_device=qsv,format=qsv[v]" ...
       -map "[v]" -c:v h264_qsv -preset veryfast -b:v 20M -bf 0 -g <fps*seg> ...
       -f segment -segment_time 1 -segment_format mpegts -reset_timestamps 0 buffer\s_%06d.ts
```
`-bf 0` обязателен (иначе «Non-monotonic DTS»). GOP = fps × segment_time, чтобы каждый
сегмент начинался с ключевого кадра.

## Аудиодорожки в файле

| # | Дорожка   | Что внутри                |
|---|-----------|---------------------------|
| 1 | Микс      | система + микрофон (её играют все плееры, Discord, Telegram) |
| 2 | Система   | только звук ПК            |
| 3 | Микрофон  | только микрофон           |

Если `SeparateAudioTracks = false` — только дорожка 1.
Редактор умеет менять громкость дорожек 2/3 отдельно и пересобирать микс.

---

## Структура проекта

```
ClipBar\
  ClipBar.sln
  ARCHITECTURE.md
  tools\ffmpeg.exe, ffprobe.exe
  src\ClipBar\
    App.xaml(.cs)            запуск, один экземпляр, трей, связка сервисов
    app.manifest             PerMonitorV2 DPI
    Styles\Theme.xaml        дизайн-токены CB.* (цвета/стили Game Bar)
    Core\                    общие контракты — менять только согласованно
      Contracts.cs           ICaptureEngine, IAudioHub, IHotkeyService, IClipLibrary, INotifier, IShell, ClipInfo
      AppSettings.cs         все настройки (JSON в %AppData%\ClipBar\settings.json)
      SettingsService.cs     Load/Save + событие Changed
      HotkeyBinding.cs       HotkeyAction, HotkeyBinding ("Ctrl+Alt+F10" ⇄ объект)
      Actions.cs             действия пользователя (сохранить откат, запись, скрин, mic…)
      AppPaths.cs            пути: ffmpeg, buffer, thumbs, logs
      Ffmpeg.cs              запуск ffmpeg/ffprobe, прогресс, мягкая остановка
      Log.cs                 лог в %LocalAppData%\ClipBar\logs
      AppServices.cs         сервис-локатор
    Capture\                 CaptureEngine — захват, буфер отката, запись, скриншоты, выбор энкодера
    Audio\                   AudioHub — WASAPI, ресэмплинг, микс, named pipes, уровни
    Hotkeys\                 HotkeyService — глобальные хоткеи (RegisterHotKey)
    Library\                 ClipLibrary — список клипов, превью, метаданные
    Editor\                  экспорт/обрезка через ffmpeg
    UI\
      MainWindow.xaml        главное окно (FluentWindow + Mica + NavigationView)
      ShellService.cs        навигация, оверлей, выход
      Pages\                 Home, Gallery (Коллекция), Editor, Settings
      Overlay\               полноэкранный оверлей с виджетами (как Win+G)
      Controls\              HotkeyRecorderBox, таймлайн редактора и т.п.
      Notifications\         тосты «Клип сохранён» в углу
```

## Правила для модулей

* Модули общаются **только** через интерфейсы из `Core\Contracts.cs` и `AppServices`.
* События сервисов могут прийти из любого потока — UI маршалит через `Dispatcher`.
* Весь текст интерфейса — **на русском**.
* Дизайн: только ресурсы `CB.*` из `Styles\Theme.xaml` + контролы WPF-UI (`ui:`).
  Тёмная тема, скругления 8 px, панели `#202020`, красная кнопка записи `#E81123`,
  иконки — `ui:SymbolIcon` (Fluent System Icons).
* Ничего не должно блокировать UI-поток: ffmpeg, диск, устройства — только async/фоновые потоки.
* Никаких исключений наружу из обработчиков UI — ловить, логировать (`Log`), показывать тост.

## Горячие клавиши по умолчанию

| Действие                     | Клавиши        |
|------------------------------|----------------|
| Сохранить откат              | Alt+F10        |
| Начать / остановить запись   | Alt+F9         |
| Скриншот                     | Alt+F1         |
| Микрофон вкл / выкл          | Alt+F8         |
| Открыть оверлей              | Alt+Z          |
| Буфер отката вкл / выкл      | Alt+Shift+F10  |

Все меняются в Настройках → «Горячие клавиши»: кликни по полю и нажми комбинацию.
