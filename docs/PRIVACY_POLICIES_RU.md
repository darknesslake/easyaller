# Post-install политики конфиденциальности

Easyaller разделяет настройки страниц OOBE Windows Setup и post-install политики конфиденциальности. Настройки OOBE остаются данными файла ответов; `PrivacyConfigurationService` принимает только `PrivacySettings` и целевой Windows, но никогда не принимает `OobeSettings`.

## Поддерживаемые цели

Сервис создаёт операции записи только для Windows 11 Pro или Enterprise, amd64, build 26100 и новее. Если для другой цели запрошена политика, план содержит ошибку, а `Apply` ничего не записывает.

## Реализованные соответствия политик

| Настройка профиля | Политика реестра | Значение | Результат |
| --- | --- | ---: | --- |
| Службы геолокации: включены | `AppPrivacy\LetAppsAccessLocation` | `1` | Принудительно разрешить |
| Службы геолокации: отключены | `AppPrivacy\LetAppsAccessLocation` | `2` | Принудительно запретить |
| Advertising ID: отключён | `AdvertisingInfo\DisabledByGroupPolicy` | `1` | Отключить advertising ID |
| Online speech recognition: отключено | `InputPersonalization\AllowInputPersonalization` | `0` | Запретить online speech services |

Полные ключи находятся в `HKLM\Software\Policies\Microsoft`. После записи сервис повторно читает каждую политику и возвращает фактическое DWORD-значение.

`notConfigured` и `userChoice` всегда ничего не делают. Они не удаляют и не заменяют существующую политику организации. Для неподдерживаемых privacy-полей, а также запросов принудительно включить advertising ID или online speech recognition сервис выдаёт предупреждение и не придумывает mapping.

Windows registry store является явным адаптером, а не частью загрузки профиля, preview, dry run или экспорта пакета. Текущий экран программы его не вызывает. Перед применением на рабочей станции нужны проверки Windows SIM и одноразовой VM.

Источник политик: [Privacy Policy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy).
