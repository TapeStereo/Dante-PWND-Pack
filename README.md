# Dante PWND Pack

[Скачать 1.1](https://github.com/TapeStereo/Dante-PWND-Pack/releases/download/v1.1/Dante-PWND-Pack-v1.1.exe) · [Все релизы](https://github.com/TapeStereo/Dante-PWND-Pack/releases)

Windows 10/11 x64, .NET Framework 4.8. Размер EXE — около 556 МБ.

Нажмите **«СДЕЛАТЬ КРАСИВО»**: программа установит DVS 4.4.1.3 и Dante Controller 4.15.0.10, сохранит оригиналы в backup и заменит файлы DVS. Оба установщика и файлы замены встроены.

Подходящие установленные версии пропускаются; более новый Controller сохраняется. Для DVS новее 4.4.1.3 замена заблокирована. Под кнопкой есть режим замены без установки.

После выполнения обязательно перезагрузите ПК. Backup хранится рядом с оригиналами в папках backup.

Для сборки поместите файлы замены в payload, оба установщика — в installers, затем выполните: powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1. Входные файлы отдельно не публикуются.

Developed by [TapeStereo](https://github.com/TapeStereo).
