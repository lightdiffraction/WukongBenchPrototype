# WukongBenchPrototype

Прототип решения задания на C#/.NET 8 для автоматизации Black Myth: Wukong Benchmark Tool.

## Что учитывает этот прототип

- результат — файл в `%TEMP%\b1\BenchMarkHistory\Tool`;
- в корне есть `FPSAvg`, `FPSMax`, `FPSMin`, `FPS95`, `CPUAvg`, `GPUAvg`;
- есть модель CPU/GPU, версия драйвера, RAM, разрешение и применённые настройки;
- есть `Records[]` с `FrameRate`, `CPUUsage`, `GPUUsage`, `CPUFrameTime`, `GPUFrameTime`;
- папка результатов по умолчанию: `%TEMP%\b1\BenchMarkHistory\Tool`;
- конфиг: `b1\Saved\Config\Windows\GameUserSettings.ini`.

## Профили

### CPU
- 1280x720
- render scale 50%
- качество 1 / ScalabilityGroups 0
- RT off
- DLSS off
- frame generation off
- VSync off
- FPS limit off

Идея: резко уменьшить работу GPU, чтобы CPU стал главным ограничителем.

### GPU
- текущая/native resolution из исходного INI (но не ниже 1920x1080)
- 100% render scale через `sg.ResolutionQuality=100`
- качество 5 / ScalabilityGroups 4
- RT on, RtxLevel=2
- DLSS off
- frame generation off
- VSync off
- FPS limit off

Идея: максимально нагрузить GPU. Frame Generation выключен, чтобы результат FPS не был завышен вставленными кадрами.

## Запуск

```powershell
dotnet build -c Release
dotnet run -c Release -- "C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool"
```

Только подготовить два INI и ничего не запускать:

```powershell
dotnet run -- "C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool" --prepare-only
```

Результаты попадут на Desktop в `WukongBenchResults`.