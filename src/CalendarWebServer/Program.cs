using System.Text;
using CosmosCalendar.Web;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
{
    Console.WriteLine($"""
        CalendarWebServer {WebServerApp.Version} — сайт «Космос календаря»

        Отдаёт страницу сайта и передаёт её запросы серверу синхронизации
        (CalendarServer): аккаунты и записи сайта — это аккаунты и записи сервера.

          -p, --port <порт>           порт сайта (8780)
              --host <адрес>          слушать один адрес (по умолчанию все)
          -u, --upstream <адрес>      сервер синхронизации (127.0.0.1:8765)
          -w, --web-root <папка>      папка сайта (CalendarWeb/site)
          -d, --data-dir <папка>      папка данных: legal.json и файл оферты
                                      (CalendarPlanner/CalendarWeb в %APPDATA%,
                                      на Linux — в ~/.local/share)
              --public-server <адрес> адрес сервера для ссылок общего доступа
              --secure-cookie <режим> auto, always или never (auto)
              --trusted-proxy <адрес> прокси, которому верить в X-Forwarded-For

        Остальные настройки — в appsettings.json рядом с программой.
        """);
    return 0;
}

try
{
    await WebServerApp.Build(args).RunAsync();
    return 0;
}
catch (Exception failure) when (failure is IOException or FormatException or InvalidOperationException)
{
    Console.Error.WriteLine($"Сайт не запустился: {failure.Message}");
    return 1;
}
