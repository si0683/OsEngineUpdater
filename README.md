# OsEngine Updater

Утилита для обновления локальных копий **[OsEngine](https://github.com/AlexWan/OsEngine)** прямо из официального репозитория.

Принцип: **обновляются только официальные файлы** — всё, чего нет в репозитории (ваши индикаторы, роботы, папки), не трогается вообще. Список ваших папок вести не нужно.

---

## Возможности

- **Incremental** — докачивает только изменившиеся официальные файлы (GitHub Compare API).
- **Mirror** — приводит движок к репозиторию **1:1** (добавляет отсутствующие, обновляет отличающиеся, удаляет удалённые официальные файлы) по git-blob-SHA.
- **Bootstrap** — разворачивает **новую** копию OsEngine из архива GitHub (ручное скачивание не нужно).
- **Watch** — проверяет обновления каждые N минут.
- **Планировщик задач** — автозапуск при входе и по интервалу (уникальное задание на каждую копию).
- Смена архитектуры отслеживается: переименованные/перенесённые/удалённые официальные файлы отражаются и у вас.
- Защищённые пути (`bin/`, `obj/`, `.vs/`, `.git/`, `*.user`) и ваши файлы не изменяются.

---

## Требования

- Windows, PowerShell 5.1+.
- Доступ в интернет (GitHub).
- Для сборки после обновления — `.NET SDK` (`dotnet` в PATH) и включённый `BuildAfterUpdate`.
- Для задания в Планировщике — запуск от имени **Администратора** (обычно).

---

## Установка

1. Скопируйте папку `OsEngineUpdater` в **корень копии OsEngine** (туда, где лежит папка `project`).
2. При необходимости отредактируйте `UpdateOsEngine.config.json`.

Утилита сама находит корень копии по пути `project\OsEngine\OsEngine.csproj`, поэтому может лежать и глубже. Состояние, манифест и лог хранятся в `<копия>\.osengine-updater\` — они не таскаются вместе с папкой утилиты.

---

## Быстрый старт

### Новая копия (с нуля)
1. Положите папку `OsEngineUpdater` в пустую директорию.
2. Запустите `bootstrap_update.cmd` — утилита скачает архив OsEngine и развернёт копию рядом.
   - Или в конкретную папку: `bootstrap_update.cmd -TargetDir "D:\MyOsEngine"`.

### Обновление существующей копии
- `start_update.cmd` — быстрая инкрементальная синхронизация.
- `mirror_update.cmd` — привести движок к репозиторию 1:1.
- `watch_update.cmd` — авто-режим, проверять каждые N минут (окно держать открытым).
- `install_update_task.cmd` — поставить задание в Планировщик; `status_update_task.cmd` / `uninstall_update_task.cmd` — статус/удаление.

---

## Команды

| Файл | Что делает |
|---|---|
| `bootstrap_update.cmd` | Развернуть новую копию из архива. |
| `start_update.cmd` | Инкрементальное обновление изменённых официальных файлов. |
| `mirror_update.cmd` | Полное соответствие движка репозиторию (1:1). |
| `watch_update.cmd` | Слежение за обновлениями каждые N минут. |
| `install_update_task.cmd` | Зарегистрировать задание в Планировщике задач. |
| `status_update_task.cmd` | Показать состояние задания. |
| `uninstall_update_task.cmd` | Удалить задание. |

Скрипт также запускается напрямую с параметрами:

```powershell
powershell -ExecutionPolicy Bypass -File UpdateOsEngine.ps1 [параметры]
```

| Параметр | Описание |
|---|---|
| `-Mirror` | Полное соответствие движка репозиторию (git-blob-SHA). |
| `-FullSync` | Полная перекачка из архива (если отстали сильно). |
| `-Bootstrap` | Развернуть новую копию из архива. |
| `-TargetDir <путь>` | Куда разворачивать при `-Bootstrap`. |
| `-CheckOnly` | Только проверить, ничего не менять. |
| `-NoBuild` | Не собирать проект после обновления. |
| `-Watch` | Цикл проверки каждые `WatchIntervalMinutes`. |
| `-Silent` | Тихий режим + запись в `update.log` (для Планировщика). |

Примеры:

```powershell
powershell -ExecutionPolicy Bypass -File UpdateOsEngine.ps1 -CheckOnly
powershell -ExecutionPolicy Bypass -File UpdateOsEngine.ps1 -Mirror
powershell -ExecutionPolicy Bypass -File UpdateOsEngine.ps1 -Mirror -Watch
```

---

## Конфигурация — `UpdateOsEngine.config.json`

```json
{
  "Owner": "AlexWan",
  "Repo": "OsEngine",
  "Branch": "master",
  "GitHubToken": "",
  "WatchIntervalMinutes": 15,
  "ProtectedPaths": [
    "**/bin/**",
    "**/obj/**",
    "**/.vs/**",
    "**/.git/**",
    "**/*.user"
  ],
  "UserProtectedPaths": [],
  "BuildAfterUpdate": false,
  "BuildCommand": "dotnet build \"project\\OsEngine\\OsEngine.csproj\" -c Debug"
}
```

| Поле | Назначение |
|---|---|
| `Owner`, `Repo`, `Branch` | Источник на GitHub. |
| `GitHubToken` | Личный токен (повышает лимит GitHub API). Пусто — анонимно. |
| `WatchIntervalMinutes` | Интервал проверки в минутах (для `-Watch` и Планировщика). |
| `ProtectedPaths` | Системные пути, которые никогда не трогаются. |
| `UserProtectedPaths` | Ваши дополнительные исключения (необязательно). |
| `BuildAfterUpdate` | Собирать проект после обновления (`true`/`false`). |
| `BuildCommand` | Команда сборки. |

Интервал задания можно переопределить при установке: `install_update_task.cmd -IntervalMinutes 30`.
После правки `WatchIntervalMinutes` **переустановите задание**, иначе Планировщик оставит старый интервал.

---

## Как это работает

- **Правило «только официальные файлы».** Утилита получает дерево файлов из репозитория и изменяет лишь пути, которые в нём есть. Ваши файлы/папки в дереве отсутствуют — поэтому не трогаются.
- **Incremental.** Сравнивает официальный коммит с сохранённым в `<копия>\.osengine-updater\state.json`, качает только изменения (Compare API) и удаляет те официальные файлы, что исчезли из репозитория (по манифесту).
- **Mirror.** Считает git-blob-SHA локальных файлов и сверяет с деревом GitHub: добавляет отсутствующие, обновляет отличающиеся, удаляет лишние официальные.
- **Защита.** `bin/`, `obj/`, `.vs/`, `.git/`, `*.user` и всё из `UserProtectedPaths` не трогается. Именно поэтому собранная `bin\Debug\OsEngine.dll` (с вашими доработками) не перезаписывается.

---

## Ограничения и важное

- **`bin/` защищён намеренно.** Официальные файлы внутри `bin/Debug` (бинарники, плагины, примеры) не синхронизируются, чтобы не стереть вашу сборку. Нужны и они — уберите `**/bin/**` из `ProtectedPaths` (на свой риск).
- **Ваши правки официальных файлов** будут перезаписаны при `-Mirror`/обновлении. Нужный файл добавьте в `UserProtectedPaths`.
- **Нет само-восстановления** в incremental: локально удалённый официальный файл вернётся только при изменении в upstream — либо через `-Mirror` / `-FullSync`.
- **Ваши файлы остаются** — поэтому папка целиком не идентична репозиторию; 1:1 будет только для официальных файлов вне защищённых путей.
- **Git-подмодуль** `related projects/FinamApi/finam-trade-api` — это gitlink, он не зеркалится.
- **Лимит GitHub API** — анонимно 60 запросов/час; при частом `-Watch` на нескольких копиях задайте `GitHubToken`.
- **Bootstrap** ставит веб-метку (`Zone.Identifier`) снимает автоматически, но при ручном скачивании ZIP из браузера не забудьте `Unblock-File` (иначе MSBuild падает на `Properties\Resources.resx`).

---

## Файлы

```
OsEngineUpdater/
  bootstrap_update.cmd
  start_update.cmd
  mirror_update.cmd
  watch_update.cmd
  install_update_task.cmd
  status_update_task.cmd
  uninstall_update_task.cmd
  UpdateOsEngine.ps1
  OsEngineUpdateTask.ps1
  UpdateOsEngine.config.json
```

Состояние на копию создаётся автоматически: `<копия>\.osengine-updater\{state.json, official-manifest.txt, update.log}`.

---

## Автор

SidorenkoVA — [@si0683](https://github.com/si0683)

Основано на проекте [AlexWan/OsEngine](https://github.com/AlexWan/OsEngine).
