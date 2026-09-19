# Сторонние компоненты

Лицензия MIT в `LICENSE` относится к коду VidCropper.

## .NET и ASP.NET Core

Portable-сборка содержит .NET 10 и ASP.NET Core, предоставляемые Microsoft и участниками проектов под MIT и условиями сторонних компонентов. Оригинальные файлы лицензий и `THIRD-PARTY-NOTICES.TXT` из использованных runtime-пакетов сохраняются в папке `licenses` архива.

- https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt
- https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt

## FFmpeg

Публичный архив VidCropper не содержит бинарных файлов FFmpeg. При первом запуске через `Start.cmd` приложение загружает FFmpeg 7.0.2 essentials непосредственно из опубликованного релиза Gyan.dev, проверяет SHA-256 и сохраняет оригинальные LICENSE и README рядом с инструментами. Загрузка происходит на компьютере пользователя; исходные видео никуда не отправляются.

- Сборка: https://github.com/GyanD/codexffmpeg/releases/tag/7.0.2
- FFmpeg: https://ffmpeg.org/
- Лицензионные условия: https://ffmpeg.org/legal.html

Эта сборка FFmpeg распространяется её поставщиком под GPLv3. MIT-лицензия VidCropper не заменяет лицензии FFmpeg и его зависимостей. FFmpeg вызывается как отдельная программа. Если вы самостоятельно распространяете архив с включённым FFmpeg, необходимо отдельно выполнить условия лицензий этой сборки, в том числе требования к соответствующему исходному коду.
