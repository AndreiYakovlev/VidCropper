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

## yt-dlp и Deno

Бинарные файлы не входят в поставку VidCropper. По запросу пользователя они скачиваются из официальных GitHub Releases в локальную папку `tools` и вызываются как отдельные программы.

- yt-dlp: https://github.com/yt-dlp/yt-dlp — исходники под Unlicense; Windows executable включает компоненты GPLv3+, условия опубликованы в разделе Licensing проекта.
- Deno: https://github.com/denoland/deno — MIT и лицензии включённых зависимостей: https://github.com/denoland/deno/blob/main/LICENSE.md

При самостоятельном распространении архива с включёнными инструментами необходимо выполнить условия их лицензий. MIT-лицензия VidCropper их не заменяет.

## AI Upscaler

AI-исполнители скачиваются по требованию непосредственно из закреплённых релизов авторов и запускаются отдельными неизменёнными процессами. Бинарные файлы и веса не включены в portable-архив VidCropper. Их лицензии не заменяются лицензией проекта.

- **SPAN-ncnn-vulkan**, TNTwise / Upscayl: GNU AGPL v3, файл `LICENSE` сохраняется из архива. Соответствующие исходники: https://github.com/TNTwise/SPAN-ncnn-vulkan/tree/20240831-055257 .
- **Nomos8k SPAN OTF Weak / Medium / Strong**, Helaman (Philip Hofmann): CC BY 4.0; преобразованные NCNN-веса поставлены в архиве SPAN. Авторские карточки: https://openmodeldb.info/models/4x-Nomos8k-span-otf-weak , https://openmodeldb.info/models/4x-Nomos8k-span-otf-medium , https://openmodeldb.info/models/4x-Nomos8k-span-otf-strong . Условия: https://creativecommons.org/licenses/by/4.0/ . VidCropper веса не изменяет.
- **Real-ESRGAN / AnimeVideo v3**, Xintao Wang и участники: BSD 3-Clause; https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0 .
- **Real-ESRGAN-ncnn-vulkan**: MIT; https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan/tree/v0.2.0 . Оригинальные тексты лицензий находятся в `licenses/ai` и копируются рядом с установленным пакетом.
- NCNN и включённые runtime-компоненты сохраняют собственные лицензии. Источник NCNN: https://github.com/Tencent/ncnn .

Рядом с каждым установленным пакетом создаётся `SOURCES.txt` со ссылками и атрибуцией. При самостоятельном распространении сборок с включёнными AI-пакетами необходимо выполнить условия лицензий соответствующих исполнителей, моделей и зависимостей, включая требования к исходному коду. Отдельный процесс не отменяет лицензионных обязательств.
