# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## О проекте

**CalendarWebServer** — бэкенд сайта «Космос календаря» на ASP.NET Core
(.NET 10): отдаёт сайт ([CalendarWeb](https://github.com/SayxZero/CalendarWeb),
в рабочей копии — `../CalendarWeb`) и передаёт его запросы `/api/*` серверу
синхронизации ([CalendarServer](https://github.com/SayxZero/CalendarServer),
`../CalendarServer`). Своих данных у него нет: сайт — ещё одно устройство
аккаунта. Почему так, а не прямо к `server.db`, — [DesDoc.md](DesDoc.md),
раздел 1.1; пользовательское описание — [README.md](README.md).

Сабмодуль зонтичного репозитория
[CosmosCalendar](https://github.com/SayxZero/CosmosCalendar) (`..`): там общий
`CLAUDE.md` (порядок коммитов в сабмодулях, правила проверок, окружение),
`DESDOC.md` и `ISSUES.md` с граблями.

## Сборка и тесты

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build CalendarWebServer.sln
dotnet test CalendarWebServer.sln                                   # 52 теста + 1 пропущен
WEB_TEST_SERVER=127.0.0.1:8766 dotnet test CalendarWebServer.sln    # и тест с настоящим сервером
```

- Пакеты тестов — **версии из локального кэша NuGet** (`MSTest` 2.2.10,
  `Microsoft.NET.Test.Sdk` 17.3.0, `Newtonsoft.Json` 13.0.1 поверх уязвимой
  10.0.3 из test host). Новые версии потребуют восстановления из nuget.org.
- У самого сервера пакетов нет — только `Microsoft.AspNetCore.App`. Новый пакет
  в `src/` — решение, а не мелочь: спросить владельца.
- `WEB_TEST_SERVER` — только **тестовый** сервер (свой порт и папка данных):
  тест регистрирует аккаунт.

## Запуск для проверок

Боевой сервер синхронизации (служба на 8765) не трогать и тестовые аккаунты на
нём не заводить. Для проверки — второй сервер и сайт перед ним, только на
`127.0.0.1`:

```bash
cmd //c "D:\\Prog\\Calendar\\CalendarServer\\run-server.bat" -p 8766 -d <папка в scratchpad> -noui   # в фоне
./src/CalendarWebServer/bin/Debug/net10.0/CalendarWebServer.exe --host 127.0.0.1 -p 8780 -u 127.0.0.1:8766 -w D:/Prog/Calendar/CalendarWeb/site -d <папка в scratchpad>   # в фоне
```

`run-server.bat` — только полным путём (`cmd` агента не ищет программы в
текущей папке, `../ISSUES.md`). Сайт затем открывается во встроенном браузере:
`http://127.0.0.1:8780/` (как проверять сам сайт — `../CalendarWeb/CLAUDE.md`).

## Режим MCP

`/mcp` и `/oauth/*` существуют, только когда заданы `PublicUrl`, `McpUpstream` и `McpSecret` (не короче 32
символов). Вход агента — здесь (`McpOAuth`, `McpClients`, `McpSeal`), инструменты — в `../CalendarWebMCP`, куда
`McpGateway` передаёт запрос с ключом сеанса. Базы для этого нет: всё выданное запечатано секретом.
Метод, принимающий `HttpContext` и возвращающий `Task<IResult>`, в `MapGet`/`MapPost` передаётся только
через `Answering()`, иначе результат молча выбрасывается (`../ISSUES.md`).

## Соглашения

- Сообщения людям (ошибки API, журнал, справка) — по-русски; идентификаторы и
  комментарии — по-английски, как в остальных проектах.
- Ошибка API — всегда `{"error": "<код>", "message": "<по-русски>"}`, как у
  сервера синхронизации (`ApiProxy.Error`).
- Новый маршрут сервера синхронизации, нужный сайту, добавляется в
  `ApiRoutes.All` — иначе он закрыт. Долгие (обмен, общий доступ) — с
  `Long: true`.
- Ключ сеанса никогда не попадает ни в тело ответа странице, ни в журнал; в
  журнал не пишется и строка запроса (там бывает токен ссылки).
- Данные владельца сайта (`legal.json`, `pricing.json`, договор оферты, APK) — только в папке
  данных (`-d`), не в `site/` и не рядом с программой: их перезаписывает
  обновление. В репозитории — только образец с пустыми полями
  (`legal.example.json`, `pricing.example.json`). Для проверок — своя папка в scratchpad с выдуманными
  значениями, а не настоящие данные владельца.
- `.bat` — CRLF и без кириллицы (`../ISSUES.md`, «Сборка и окружение Windows»).
- Команда `dotnet` в Git Bash печатает по-русски; телеметрию и логотип гасят
  переменные выше.

## Грабли

Общий список — [`../ISSUES.md`](../ISSUES.md), раздел «Сайт и его бэкенд».
