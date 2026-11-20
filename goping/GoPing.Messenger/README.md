# GoPing — WPF-клиент

**GoPing — a local messenger for you and your friends, with login via GitHub or a server token!**

Десктопный клиент для Windows (WPF, .NET 10). Текущая версия: минималистичный
вход, профиль и заглушка чатов.

## Что уже есть

- [x] Плоский минималистичный интерфейс: без свечения, градиентов и анимаций,
      1 px границы, спокойные цвета
- [x] Вход через GitHub (браузер + опрос состояния)
- [x] Вход по серверному токену `GOPING-XXXXXXXXXXXX`
- [x] Запоминание сессии между запусками (DPAPI, файл на пользователя)
- [x] Автоматическое продление access-токена через refresh
- [x] Профиль: никнейм, username, описание, загрузка/удаление аватарки
- [x] Выход из аккаунта
- [ ] Список чатов и переписка — следующий шаг

## Как собрать и запустить

```powershell
# из корня репозитория
dotnet build GoPing.sln
dotnet run --project GoPing.Messenger\GoPing.Messenger.csproj
```

Собранное приложение: `GoPing.Messenger\bin\Debug\net10.0-windows\GoPing.exe`.

Сервер по умолчанию — `http://localhost:8080`. Другой адрес задаётся
переменной окружения `GOPING_API_URL`:

```powershell
$env:GOPING_API_URL = "http://192.168.1.10:8080"
dotnet run --project GoPing.Messenger\GoPing.Messenger.csproj
```

## Вход

### Через GitHub

Нажмите **Continue with GitHub** — откроется браузер. После подтверждения
вернитесь в приложение: клиент сам подхватит сессию. Никнейм и аватар
заполняются из GitHub при первом входе; потом их можно менять — повторный
вход внесённые изменения не затрёт.

Требует настроенного OAuth App на сервере (см. `server/README.md`).

### По серверному токену

Администратор сервера выдаёт токен вида `GOPING-Ab3_x-9Qw2Z0`. Вставьте его
в поле **Server token** и нажмите **Sign in**. Первый вход создаёт аккаунт и
навсегда привязывает к нему токен; дальше этот же токен открывает тот же
профиль.

Приложение запоминает токен, поэтому повторно вводить его не нужно.

## Где хранится сессия

`%LOCALAPPDATA%\GoPing\session.dat` — зашифрован Windows DPAPI для текущего
пользователя. Файл нельзя перенести на другого пользователя или машину.
**Sign out** удаляет его.

## Структура

```
GoPing.Messenger/
├── App.xaml, App.xaml.cs        общие стили и восстановление сессии
├── LoginWindow.xaml(.cs)        вход: GitHub + серверный токен
├── MainWindow.xaml(.cs)         профиль и заглушка чатов
├── Models/ApiModels.cs          DTO сервера
├── Services/
│   ├── ApiClient.cs             HTTP, refresh, GitHub-опрос, аватарки
│   ├── ApiException.cs          ошибка API с кодом
│   └── SessionStore.cs          DPAPI-хранилище сессии
└── GoPing.Messenger.csproj
```

## Дизайн

- Фон `#FAFAFA`, поверхности `#FFFFFF`, текст `#18181B`.
- Акцент — спокойный синий `#2563EB`, опасность — `#DC2626`.
- Никаких теней, blur, glow и переходов: только рамки и отступы.
- Все стили — в `App.xaml`, окна не содержат локальных «украшений».
