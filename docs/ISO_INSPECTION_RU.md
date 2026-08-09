# Проверка ISO только для чтения

Easyaller умеет проверить локально выбранный Windows ISO до будущего сценария создания USB. Функция пока не подключена к интерфейсу и не выбирает, не форматирует, не размечает, не инициализирует и не записывает диски.

## Что проверяется

Запрос требует абсолютный локальный путь к `.iso`, положительное ограничение размера и выбранную цель развёртывания. Лимит по умолчанию - 12 GiB. До монтирования Easyaller проверяет наличие файла, размер и считает SHA-256.

Только на Windows неизменяемый неинтерактивный PowerShell-запрос монтирует ISO с `Mount-DiskImage -Access ReadOnly`, находит его том и только читает:

- наличие `setup.exe`, `sources/setup.exe` и `sources/boot.wim`;
- ровно одного образа установки: `sources/install.wim` либо `sources/install.esd`;
- список образов, ID редакций, архитектуру и версию через `Get-WindowsImage`;
- что все найденные образы amd64 и есть выбранная редакция Professional или Enterprise.

В блоке `finally` запрос всегда вызывает `Dismount-DiskImage` по пути ISO, включая случай ошибки проверки. Если размонтирование не удалось, команда завершается ошибкой и рабочий результат не возвращается. Путь ISO передаётся через окружение дочернего процесса, а не подставляется в PowerShell-команду.

`Mount-DiskImage` поддерживает доступ `ReadOnly`, `Get-Volume` принимает объект дискового образа, а `Dismount-DiskImage` отсоединяет ISO по полному пути. [Mount-DiskImage](https://learn.microsoft.com/en-us/powershell/module/storage/mount-diskimage), [Get-Volume](https://learn.microsoft.com/en-us/powershell/module/storage/get-volume), [Dismount-DiskImage](https://learn.microsoft.com/en-us/powershell/module/storage/dismount-diskimage). `Get-WindowsImage` читает метаданные выбранного install-образа. [Get-WindowsImage](https://learn.microsoft.com/en-us/powershell/module/dism/get-windowsimage)

## Границы

- Загрузка ISO и сверка с хешем издателя не входят в задачу. Оператор получает ISO в одобренном канале и сам сравнивает выведенный SHA-256.
- На macOS и Linux проверка вернёт блокирующий результат и не запустит shell.
- Успешная проверка не разрешает запись на USB. Подтверждение, повторная проверка устройства и движок записи будут отдельными задачами.
