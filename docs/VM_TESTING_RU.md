# Проверка в VM

Эта инструкция описывает обязательный процесс проверки deployment package Easyaller в изолированной виртуальной машине Windows 11. Она не разрешает тестирование на физических рабочих станциях, физических дисках, production-доменах или съёмных носителях.

## Предварительные условия

- Используйте VM Windows 11 с документированными требованиями: Generation 2, UEFI, Secure Boot, vTPM 2.0, не менее 4 ГБ памяти, не менее 64 ГБ виртуального диска и два виртуальных процессора.
- Используйте checkpoint или snapshot гипервизора. Это точка сброса теста, а не backup production-данных.
- Получите ISO от администратора или из другого одобренного официального источника Microsoft. Easyaller не должен скачивать ISO, а ISO не должен попадать в Git.
- Храните ISO, сгенерированный пакет, VM-диск, скриншоты, логи и доказательства вне этого репозитория. Запишите SHA-256 каждого ISO, `install.wim` или `install.esd`, файла ответов и deployment manifest.
- Сначала завершите подходящую проверку Windows SIM. Успех Windows SIM не заменяет тест в VM.

Microsoft документирует требования к VM Windows 11 и Hyper-V checkpoints: [требования Windows 11](https://learn.microsoft.com/en-us/windows/whats-new/windows-11-requirements), [Hyper-V checkpoints](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/checkpoints).

## Настройка изолированной VM

1. Создайте новую пустую VM только с одним виртуальным диском. Не подключайте диск хоста, USB, физический диск или production network share.
2. Настройте VM для ISO и требуемых UEFI, Secure Boot, vTPM, памяти, диска и CPU.
3. Используйте изолированную или NAT test network. Отключайте её, когда сценарий требует offline OOBE. Никогда не используйте production domain credentials.
4. Передавайте тестируемый пакет изолированным способом и перед стартом VM проверяйте хэш deployment manifest.
5. Создайте checkpoint `before-oobe` после готовности аппаратных настроек VM и носителя, но до запуска Windows Setup.

## Последовательность теста

1. Загрузите Windows Setup и вручную выберите единственный пустой VM-диск, подтвердив его virtual-disk identity и размер. Easyaller не автоматизирует выбор внутреннего диска.
2. Наблюдайте OOBE. Запишите фактические edition, display version, build, архитектуру, locale и итог каждой настроенной страницы OOBE.
3. Войдите вручную во временную учётную запись `ProvisioningAdmin` только если её создал сгенерированный пакет. Отметьте, что пароль был показан один раз, но не записывайте сам пароль.
4. Проверьте payload manifest verification, первый запуск и resume. До любого разрушительного теста или эксперимента с cleanup создайте второй checkpoint `after-first-login`.
5. Проверяйте network, proxy, applications и instructions только нейтральными test values и fixtures. Не добавляйте VM в production domain.
6. Для mock domain-join scenario записывайте в evidence только симулированный success или failure. Не запускайте команды присоединения к домену и не передавайте directory credentials, пока не появятся отдельный executor и его одобрение.
7. Переходите к cleanup только после появления evidence ожидаемого административного доступа. Текущая машина состояний только планирует cleanup и не имеет Windows account-management adapter.
8. Сравните runtime version gate с manifest пакета. Несовпадение должно блокировать проверенные действия, неизвестный build должен предупреждать и пропускать их.

## Evidence и условия остановки

Скопируйте [`fixtures/vm-evidence.template.json`](fixtures/vm-evidence.template.json) в локальную папку evidence, например `%LOCALAPPDATA%\Easyaller\Validation\VM`. Замените placeholders фактами одного прогона VM. Template безопасно хранить в Git, completed evidence нельзя.

Остановите прогон и сохраните checkpoint при любом из условий:

- Windows Setup показывает диск, отличный от ожидаемого пустого VM-диска.
- Хэш ISO, image, answer file, manifest или payload отличается от записанного входа.
- Windows SIM показывает warning или error для того же набора входов.
- Runtime gate сообщает `blocked` или неожиданное warning.
- Скрипт запрашивает secret, production credential, произвольную команду, AutoLogon, disk operation или network download.

Не называйте пакет VM-validated только потому, что Windows установилась. Отмечайте compatibility entry как `VmValidated` лишь после passing evidence полной плановой матрицы для точных ISO и package inputs.

## Сброс и очистка test secrets

После каждого прогона либо примените checkpoint `before-oobe`, либо удалите VM и цепочку её виртуальных дисков. Удалите локальную копию сгенерированного package и completed evidence, если в них есть organization-specific data. Удаляйте test-only accounts из VM только одобренным cleanup flow после подтверждения administrator access.

Никогда не помещайте сгенерированные пароли временной учётной записи, runtime domain credentials, tokens, VM-диски, snapshots, скриншоты или completed evidence в репозиторий, issue tracker или public release assets.
