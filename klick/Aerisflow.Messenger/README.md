# Aerisflow Messenger

WPF (Windows) desktop messenger client — текущая версия: **PC (WPF)**.

## Текущий статус

- [x] Базовый UI в стиле tray-меню (Aero-glass: brushes, MenuButton, Slider, SwitchToggle, PopPanel)
- [x] Окно входа (login)
- [x] Окно регистрации (register) с локальной валидацией
- [ ] Чат (message list, composer) — следующий шаг
- [ ] Интеграция с сервером: Go + PostgreSQL (`POST /auth/login`, `POST /auth/register`)

## Планы

- Следом — окно чата в том же стиле.
- Сейчас backend: Go + PostgreSQL (auth).
- Позже — браузерная версия и мобильная версия на Flutter (общие design-токены из brushes/стилей).

## Как собрать

```
dotnet build Aerisflow.Messenger/Aerisflow.Messenger.csproj
dotnet run --project Aerisflow.Messenger
```
