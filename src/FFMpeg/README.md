# Нативные библиотеки FFmpeg

Сюда кладутся бинарники FFmpeg, которые загружает `MediaToolkitNet.FFmpeg`.
Сами файлы не отслеживаются git (см. `.gitignore` в корне).

```
src/FFMpeg/Windows/   avutil-59.dll, avcodec-61.dll, avformat-61.dll,
                      swscale-8.dll, swresample-5.dll, avdevice-61.dll
src/FFMpeg/Linux/     libavutil.so.59, libavcodec.so.61, libavformat.so.61,
                      libswscale.so.8, libswresample.so.5, libavdevice.so.61
src/FFMpeg/MacOS/     libavutil.59.dylib, libavcodec.61.dylib, libavformat.61.dylib,
                      libswscale.8.dylib, libswresample.5.dylib, libavdevice.61.dylib
```

## Требуется FFmpeg 7.x

Биндинги привязаны к мажорным версиям **avutil 59 / avcodec 61 / avformat 61**.
Загрузчик проверяет их в рантайме и отказывается работать с другими, потому что
публичные структуры FFmpeg не сохраняют ABI между мажорными релизами, а
`MediaToolkitNet.FFmpeg` читает несколько полей напрямую. Полный список этих полей и
их смещений — в одном файле: `src/MediaToolkitNet.FFmpeg/Native/AbiLayout.cs`.

`avdevice` необязателен: без него не работает только перечисление устройств через
FFmpeg (`FFmpegDeviceEnumerator`).

## Где ещё ищутся библиотеки

`MediaToolkitNet.Interop.NativeSearchPaths` пробует по порядку:

1. каталоги, добавленные через `NativeSearchPaths.Prepend(...)`;
2. каталог приложения и его подпапки `native/` и `native/<Платформа>/`;
3. `runtimes/<RID>/native/` — раскладка NuGet;
4. `src/FFMpeg/<Платформа>/`, найденная подъёмом вверх от каталога сборки —
   это удобно при запуске прямо из `bin/`;
5. системный поиск операционной системы.
