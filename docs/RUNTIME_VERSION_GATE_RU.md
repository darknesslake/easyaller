# Runtime-проверка версии Windows

Перед тем как будущая first-boot операция использует deployment package, Easyaller может прочитать установленную редакцию Windows, display version, build и архитектуру, а затем сравнить их и с deployment manifest, и с выбранным профилем.

Windows provider работает только на чтение. Он читает `EditionID`, `DisplayVersion` и `CurrentBuildNumber` из `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` и использует архитектуру запущенной ОС. На другой операционной системе или при отсутствии значений он возвращает предупреждение и не разрешает проверенные действия.

| Состояние | Значение | Проверенные действия |
| --- | --- | --- |
| `ready` | Runtime, manifest, профиль и документированный каталог совпали | Разрешены |
| `warning` | Runtime недоступен или build отсутствует в каталоге | Пропускаются |
| `blocked` | Runtime не совпал с manifest либо manifest и профиль противоречат друг другу | Заблокированы |

Проверка требует точного совпадения редакции, архитектуры, display version и build. Неизвестные редакции, build и архитектуры не включают обходной путь. Они остаются предупреждением или блокировкой, пока каталог совместимости, Windows SIM evidence и VM validation не будут намеренно обновлены.

Текущий экран программы пока не вызывает эту проверку. Это read-only компонент для будущего first-boot execution flow. См. [Get-ComputerInfo](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/get-computerinfo) и [сведения о выпусках Windows 11](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information).
