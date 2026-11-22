<div align="center">

# NotchturneWin

**Элегантный Dynamic Island / «остров» в верхней части экрана для Windows 10 и Windows 11**

Управление музыкой, синхронные тексты песен (LRCLIB), HD-обложки, календарь и история буфера обмена — прямо на рабочем столе.

[![Latest Release](https://img.shields.io/github/v/release/apexFrog/notchturne-win?label=Release&style=for-the-badge&color=111114)](https://github.com/apexFrog/notchturne-win/releases/latest)
[![Download ZIP](https://img.shields.io/badge/Download-ZIP%20Archive-2ea44f?style=for-the-badge&logo=windows)](https://github.com/apexFrog/notchturne-win/releases/latest/download/NotchturneWin-win-x64.zip)
[![.NET 8](https://img.shields.io/badge/.NET-8.0%20WPF-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?style=for-the-badge&logo=windows11)](#требования)

---

### Быстрая загрузка

👉 **[Последний релиз (Latest Release)](https://github.com/apexFrog/notchturne-win/releases/latest)**  
📦 **[Скачать готовый ZIP-архив (`NotchturneWin-win-x64.zip`)](https://github.com/apexFrog/notchturne-win/releases/latest/download/NotchturneWin-win-x64.zip)**

</div>

---

## Возможности

- **Управление медиа (SMTC)** — работает с любым плеером, который отображается в системном медиа-виджете Windows: Spotify, Apple Music, Яндекс Музыка, YouTube Music, браузеры и др.
- **Синхронные тексты песен (LRCLIB)** — 5 режимов отображения:
  - *Караоке* (плавная подсветка)
  - *Прыгающие буквы* (Bounce)
  - *Печать с курсором* (Typing)
  - *Классический*
  - *Одна строка*
  - Перемотка трека кликом по строке и ручная настройка сдвига тайминга (`±3 сек`).
- **Плавающее окно текста** — отдельный оверлей с настройкой позиции, размера шрифта, прозрачности и режима «пропускать клики» (click-through).
- **HD-обложки** — автоматическая подгрузка обложек высокого качества из Apple Music (iTunes / Deezer) и адаптивный акцентный цвет.
- **Оформление острова, переливы и визуализатор** — выбор фона (`Black`, `Glass`, `CoverGlass`, `CoverBlack`), анимированный перелив палитры обложки (`Aurora` и `SpotifyMesh`), встроенный мини-визуализатор (`Bars`, `Dashes`, `Wave`, `Blocks`, `None`) и отображение текущей строки прямо в свёрнутом острове.
- **Гибкое позиционирование острова** — привязка к верхнему, нижнему, левому или правому краю экрана, угловой режим (*Corner Wrap*), свободное перетаскивание (`Floating`) и компактный индикатор прогресса (`Ring`, `Bar`, `Dots`, `Time`, `Capsule`) для боковых/угловых панелей.
- **Календарь и история буфера обмена** — быстрый доступ к календарю и последним скопированным записям прямо внутри острова.
- **Фильтр источников медиа** — возможность игнорировать браузеры и добавлять любые приложения в список исключений.
- **Языки интерфейса** — русский и английский (`RU` / `EN`).

---

## Установка и запуск

### Способ 1: Готовая сборка (рекомендуется)

1. Скачайте **[NotchturneWin-win-x64.zip](https://github.com/apexFrog/notchturne-win/releases/latest/download/NotchturneWin-win-x64.zip)** со страницы **[последнего релиза](https://github.com/apexFrog/notchturne-win/releases/latest)**.
2. Распакуйте архив в удобную папку.
3. Запустите `NotchturneWin.exe`.

> **Примечание:** Для запуска требуется [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).

### Управление

- **Наведите курсор** на верхний центр экрана — остров плавно раскроется.
- **Настройки** — доступны прямо внутри интерфейса острова (изменения применяются мгновенно и сохраняются в `%AppData%\NotchturneWin\settings.json`).
- **Правый клик по острову** — выход из приложения.

### Автозагрузка Windows

Для добавления программы в автозагрузку запустите `autostart.bat` (после сборки через `build.bat`).

---

## Сборка из исходников

### Требования

- Windows 10 (сборка 19041+) или Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Быстрая сборка

Запустите `build.bat` — скрипт соберёт один исполняемый файл `dist\NotchturneWin.exe`.

Или выполните вручную в терминале:

```bash
# Запуск в Release-режиме
dotnet run -c Release

# Публикация в один exe-файл
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

---

## Структура проекта

```text
NotchturneWin/
├── Controls/
│   ├── CoverShimmerLayer.cs     # Анимированный фон по палитре обложки (Aurora / SpotifyMesh)
│   ├── LyricsView.xaml          # Контрол синхронных текстов песен
│   ├── LyricsView.xaml.cs
│   ├── MiniVisualizer.cs        # Мини-визуализатор в острове
│   └── SideProgressIndicator.cs # Индикатор прогресса для боковых/угловых режимов
├── Models/
│   └── AppSettings.cs           # Модель и сохранение настроек
├── Services/
│   ├── ArtworkService.cs        # Загрузка HD-обложек (iTunes / Deezer)
│   ├── LyricsService.cs         # Получение синхронных текстов (LRCLIB)
│   ├── MediaService.cs          # Интеграция с Windows SMTC
│   └── MetadataHelper.cs        # Очистка и нормализация метаданных треков
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .cs        # Основное окно острова
├── FloatingLyricsWindow.xaml/.cs# Плавающее окно текста песни
├── SettingsWindow.xaml / .cs    # Окно настроек
├── NotchturneWin.csproj
├── build.bat                    # Сборка в один exe
└── autostart.bat                # Добавление в автозагрузку Windows
```
