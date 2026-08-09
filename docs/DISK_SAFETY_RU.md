# Модель защиты съёмных дисков

В Easyaller появилась read-only модель инвентаризации и выбора дисков. Она не форматирует, не инициализирует, не размечает, не монтирует, не размонтирует, не очищает, не меняет атрибуты и не записывает данные на диск.

Windows inventory provider читает данные `Get-Disk` и `Win32_DiskDrive` через фиксированный non-interactive PowerShell query. Он собирает номер диска, vendor, friendly name, serial number, неизменяемый `UniqueId`, тип шины, removable status, размер и флаги system, boot, read-only и offline. Эти поля соответствуют документированной модели Windows `MSFT_Disk`. [Справка MSFT_Disk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk), [справка Get-Disk](https://learn.microsoft.com/en-us/powershell/module/storage/get-disk)

## Допустимость

Диск по умолчанию не выбирается. Будущий UI должен требовать явный клик по видимому кандидату, а safety service разрешает confirmation только при одновременном выполнении всех условий:

- диск сообщает непустой неизменяемый `UniqueId`;
- он сообщает removable media;
- его шина USB, SD или MMC;
- это не system и не boot disk;
- диск online, writable и имеет положительный размер.

Все остальные диски блокируются, включая internal, virtual, unknown-bus, offline, read-only, system и boot disks.

## Защита от hot-swap

Выбор сохраняет неизменяемый `UniqueId`, необязательный serial number и номер диска, показанный в момент выбора. Перед любым будущим confirmation или write inventory нужно обновить и повторно проверить выбор:

- отсутствующий ID, дублирующийся ID, изменившийся serial number или другой диск под тем же номером блокирует поток;
- диск с изменившимся номером, но совпадающими неизменяемыми ID и serial, остаётся тем же выбранным устройством;
- повторная проверка допустимости обязательна, поэтому устройство, ставшее boot, system, offline, read-only или non-removable, блокируется.

Эта задача намеренно не включает destructive confirmation, ISO inspection, USB writing и desktop UI. Это отдельные следующие задачи.
