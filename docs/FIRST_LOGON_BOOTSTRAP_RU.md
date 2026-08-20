# Bootstrapper первого входа

Bootstrapper включается только явной опцией deployment API. Интерфейс программы пока его не включает.

## Предварительные условия

Перед добавлением `FirstLogonCommands` в файл ответов обязательны все условия:

- запрос явно включает bootstrapper первого входа;
- профиль использует режим запуска `FirstLogon`;
- активная временная локальная учётная запись называется строго `ProvisioningAdmin`;
- в пакете находится обычный файл приложения `$OEM$/$1/ProgramData/Easyaller/payload/Easyaller.exe`.

Генератор добавляет ровно один фиксированный упорядоченный `SynchronousCommand` в проход `oobeSystem`. Он указывает только на `C:\ProgramData\Easyaller\scripts\Start-EasyallerBootstrap.ps1`. Переиспользуемый профиль не может передать командную строку, текст скрипта, имя учётной записи, пароль, значение домена или аргументы. AutoLogon по-прежнему отсутствует.

## Последовательность bootstrapper

После ручного входа администратора скрипт сначала проверяет хэши каждого файла из payload-manifest и требует запись для упакованного приложения Easyaller. Только в первоначальном режиме он записывает точную фиксированную команду в `HKLM\Software\Microsoft\Windows\CurrentVersion\RunOnce` под именем `!EasyallerBootstrapResume`, после чего запускает `Easyaller.exe --resume` без ожидания GUI-процесса. Вызов для возобновления не записывает данные в `RunOnce`. Когда Easyaller создаст главное окно с точным аргументом resume, она удалит запись и сохранит локальное состояние `completed`.

Префикс `!` просит Windows отложить удаление значения `RunOnce` до завершения команды bootstrapper. Это одноразовый механизм продолжения, а не надёжный планировщик повторных попыток: если сама команда bootstrapper завершилась, но GUI позже закрылся, Windows уже может удалить запись. Поэтому Easyaller считает отсутствие записи нормальным и никогда не создаёт её повторно. См. [справку Microsoft RunOnce](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys).

Команда использует process-scoped PowerShell execution-policy bypass только для встроенного скрипта, проверенного manifest. Она не принимает PowerShell от пользователя, не скачивает код, не меняет системную execution policy и не запускается до проверки payload.

Windows запускает `FirstLogonCommands` при первом входе администратора, после входа и до появления рабочего стола. Перед реальным использованием путь пакета и файл ответов всё равно нужно проверить в Windows SIM и одноразовой VM. См. [справку Microsoft FirstLogonCommands](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-firstlogoncommands).
