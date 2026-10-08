# WukongBenchPrototype

Прототип решения задания на C#/.NET 8 для автоматизации Black Myth: Wukong Benchmark Tool.

## Что уже учитывает этот прототип

По предоставленному JSON `1791486107`:

- результат — JSON;
- в корне есть `FPSAvg`, `FPSMax`, `FPSMin`, `FPS95`, `CPUAvg`, `GPUAvg`;
- есть модель CPU/GPU, версия драйвера, RAM, разрешение и применённые настройки;
- есть `Records[]` с `FrameRate`, `CPUUsage`, `GPUUsage`, `CPUFrameTime`, `GPUFrameTime`;
- папка результатов по умолчанию: `%TEMP%\b1\BenchMarkHistory\Tool`;
- конфиг: `b1\Saved\Config\Windows\GameUserSettings.ini`.

В присланном примере `GPUAvg=89`, `CPUAvg=1`, а среднее `GPUFrameTime` выше `CPUFrameTime`, то есть этот конкретный прогон явно GPU-bound. Это полезно для самопроверки CPU-профиля.

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

## Важное ограничение текущей версии

Самая неопределённая часть — автоматическое нажатие кнопок Unreal UI. В коде изменяются именно пары в `UISettingData` и значения `[ScalabilityGroups]`, потому что именно такая структура есть в присланном `GameUserSettings.ini`.

В коде используется:

`startup -> Enter -> Benchmark click -> Confirm click`

Координаты `Benchmark` и `Confirm` заданы нормализованно и специально вынесены в `AppConfig`.

Если пришлёшь:
1. скрин стартового меню Benchmark Tool;
2. скрин после нажатия Benchmark, где появляется Confirm;
3. желательно размер окна/разрешение;

я смогу подогнать координаты/последовательность. Ещё лучше — если пришлёшь 2–3 скрина этих экранов с курсором, можно сделать более надёжный вариант.

## Что нужно улучшить до финальной версии

1. Надёжнее проходить Unreal UI, не завязываясь на одну пару координат.
2. Проверять после запуска, что свежий JSON действительно соответствует текущему профилю.
3. Сохранять применённый INI рядом с raw JSON.
4. Добавить автоматический поиск Steam-библиотек.
5. Добавить проверку, что CPU-тест действительно CPU-bound; если нет — выдавать предупреждение.
6. При желании сделать WPF/WinForms GUI вместо консольного интерфейса.

### Важная деталь по `ImageQuality`

В присланном INI поле `UISettingData.ImageQuality` имеет значение `728`, а в JSON этого же прогона `ImageQuality=35`; поэтому я не считаю это поле прямым процентом render scale. В прототипе render scale меняется через `[ScalabilityGroups] -> sg.ResolutionQuality`, а `UISettingData` используется для остальных переключателей/пресета. Это оставляет меньше предположений о внутреннем формате игры.
