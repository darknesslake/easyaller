# Проверка Windows SIM

Эта инструкция проверяет один сгенерированный файл ответов на одном образе Windows, предоставленном администратором. Она выполняется только на Windows. Процесс не монтирует, не изменяет и не форматирует ISO, WIM, ESD, диск или USB-накопитель.

Windows SIM входит в Windows ADK. Microsoft требует повторно проверять вручную созданные файлы ответов в Windows SIM, потому что доступные настройки могут меняться. Windows SIM сравнивает файл ответов с настройками выбранного образа Windows. [Техническая справка Windows SIM](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/wsim/windows-system-image-manager-technical-reference), [рекомендации для файлов ответов](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/wsim/best-practices-for-authoring-answer-files), [Validate an Answer File](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/wsim/validate-an-answer-file)

## Входные данные и ограничения

Держите все четыре входных файла вне репозитория Easyaller:

- Официальный ISO Windows, предоставленный администратором.
- Точный `install.wim` или `install.esd` из этого ISO.
- Индекс образа для целевой редакции Pro или Enterprise.
- Сгенерированный `autounattend.xml`.

По умолчанию JSON с доказательствами записывается в `%LOCALAPPDATA%\Easyaller\Validation`. Скрипт не позволит указать выходную папку внутри репозитория. ISO, WIM, ESD, сгенерированный XML, каталог и JSON-доказательства не должны попадать в Git.

## Автоматическая предварительная проверка

Запустите PowerShell на техническом компьютере с Windows и DISM. Скрипт только читает образ и проверяет XML-политику.

```powershell
.\scripts\Validate-AnswerFile.ps1 `
  -InstallationMedia 'D:\Images\Windows11.iso' `
  -WindowsImage 'D:\Images\install.wim' `
  -ImageIndex 6 `
  -AnswerFile 'D:\Easyaller-output\autounattend.xml'
```

Скрипт проверяет корневой XML-элемент и отсутствие запрещённых секций, записывает SHA-256 хэши ISO, образа и файла ответов. Затем он вызывает DISM для выбранного индекса и сохраняет метаданные и исходный вывод в JSON. Он не создаёт каталог и не объявляет проверку Windows SIM успешной.

## Завершение проверки в Windows SIM

1. Установите Windows ADK на технический компьютер и откройте Windows System Image Manager.
2. Откройте копию выбранного `install.wim` или файл каталога из доступной для записи технической папки. Windows SIM создаст или обновит каталог для открытого образа.
3. Выберите тот же индекс образа, который указан в JSON предварительной проверки.
4. Откройте сгенерированный `autounattend.xml` в панели Answer File.
5. Выберите **Tools**, затем **Validate Answer File**.
6. Запишите точную версию ADK или Windows SIM и результат. Успех означает отсутствие предупреждений и ошибок в панели Validation.

Microsoft описывает этот этап как интерактивное действие в Windows SIM. Harness намеренно не подменяет его вымышленным положительным результатом командной строки.

После ручного шага создайте финальное доказательство:

```powershell
.\scripts\Validate-AnswerFile.ps1 `
  -InstallationMedia 'D:\Images\Windows11.iso' `
  -WindowsImage 'D:\Images\install.wim' `
  -ImageIndex 6 `
  -AnswerFile 'D:\Easyaller-output\autounattend.xml' `
  -WindowsSimResult Passed `
  -WindowsSimVersion 'Windows ADK 10.1.x, Windows SIM' `
  -WindowsSimMessage 'No warnings or errors in the Validation pane.'
```

Если Windows SIM показал предупреждение или ошибку, укажите `Failed` и перенесите сообщение в `WindowsSimMessage`. Не отмечайте запись каталога как `SchemaValidated`, пока не существует JSON с результатом `Passed` для точных хэшей ISO, образа, индекса образа и файла ответов.

## Ограничения

Это проверка схемы, а не тест в VM или на физическом ПК. Результат `Passed` не подтверждает поведение OOBE, первый запуск, сетевые условия, очистку учётной записи или USB-сценарий. Для этого понадобятся отдельные доказательства в VM и на физическом ПК.
