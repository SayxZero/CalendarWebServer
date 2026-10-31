# Установка сайта «Космос календаря» на сервер

Инструкция ставит ветку `site-only` на сервер, описанный в `SERVER-SETUP.md`, и настраивает ей новый
домен. Сайт VolleySigns при этом продолжает работать: у нового сайта свой порт, своя служба и свой файл в
nginx, а nginx перечитывает настройки без перезапуска.

Все команды этой инструкции проверены на копии сервера: Ubuntu 26.04 в контейнере Docker с systemd, nginx и
работающим соседним сайтом. На настоящем сервере ещё не запускались, поэтому перед началом выполните раздел 1.

## Что получится

| Что | Значение |
|---|---|
| Проект | `cosmos-site` |
| Порт | `8138`, только на `127.0.0.1`, в файрволе не открывается |
| Служба | `cosmos-site`, от своего пользователя |
| Папка | `/opt/cosmos-site`, данные в `/opt/cosmos-site/data` |
| Сайт nginx | `/etc/nginx/sites-available/cosmos-site` |
| Сервер синхронизации | не нужен: ветка к нему не обращается |

**Что работает:** страница-презентация, цены, юридические данные и договор оферты, ролики, инструкции.

**Что закрыто:** планер в браузере, аккаунты, MCP и скачивание приложения. Адреса `/api`, `/mcp`, `/oauth` и всё,
чего нет в списке разрешённых (`SiteOnly.cs`), отвечают 404. Кнопки «Открыть планер», «Открыть» и «Попробовать»
ведут на страницу «Планер в браузере скоро заработает».

**Заполнители:** `IP_СЕРВЕРА` и `ДОМЕН` — подставьте свои. Домен пишется без `www`.

## 1. Проверить сервер

Выполняется на сервере. Каждая команда отдельно.

```
ls -l /etc/nginx/sites-enabled
```

В списке должны быть `volleysigns` и `default-drop`. Если `default-drop` нет, поставьте его по разделу 7
`SERVER-SETUP.md`: без него запрос на незнакомый домен попадёт на случайный сайт.

```
sudo ss -tlnp
```

Порт `8138` должен быть свободен. Если занят, замените его в `deploy/cosmos-site.service` и
`deploy/nginx-cosmos-site-https.conf` до сборки архива.

```
sudo nginx -t
```

Ожидается `test is successful`. Если нет, сначала разберитесь с этим: новый сайт здесь ни при чём, но перечитать
настройки nginx не получится.

Запомните, как отвечает старый сайт, чтобы сравнить в конце:

```
curl -I https://СТАРЫЙ_ДОМЕН
```

Также нужны:

- A-запись нового домена, указывающая на `IP_СЕРВЕРА`; для `www` — такая же, если сертификат покрывает `www`;
- сертификат на новый домен от регистратора (раздел 8 `SERVER-SETUP.md`), скачанный в формате PEM.

## 2. На ПК: собрать архив и отправить

Архив собирается из ветки `site-only` и текущей копии сайта в `CalendarWeb`.

```bash
git -C D:/Prog/Calendar/CalendarWebServer switch site-only
```
```bash
cmd //c "D:\\Prog\\Calendar\\CalendarWebServer\\deploy\\make-archive.bat" -noui
```
```bash
scp "D:/Prog/Calendar/CalendarWebServer/cosmos-site.tar.gz" dan@IP_СЕРВЕРА:~/
```

Отправьте данные владельца. Они лежат вне репозитория, и архив их не содержит.

`pricing.json` лежит в `%APPDATA%\CalendarPlanner\CalendarWeb`. Файла `legal.json` с настоящими реквизитами на этом
компьютере нет: создайте его по образцу `legal.example.json`. Без него на странице не будет подвала.

```bash
scp "$APPDATA/CalendarPlanner/CalendarWeb/pricing.json" dan@IP_СЕРВЕРА:~/
```
```bash
scp "ПУТЬ_К_ФАЙЛУ/legal.json" dan@IP_СЕРВЕРА:~/
```

Если в `legal.json` указан файл договора оферты (`offerFile`), отправьте и его.

Отправьте файлы сертификата: `certificate.crt`, `certificate_ca.crt`, `certificate.key`.

```bash
scp "ПАПКА_СЕРТИФИКАТА/certificate.crt" "ПАПКА_СЕРТИФИКАТА/certificate_ca.crt" "ПАПКА_СЕРТИФИКАТА/certificate.key" dan@IP_СЕРВЕРА:~/
```

Вернитесь на основную ветку, чтобы рабочая копия осталась как была:

```bash
git -C D:/Prog/Calendar/CalendarWebServer switch master
```

## 3. На сервере: среда выполнения

Программе нужен ASP.NET Core 10 из репозитория Ubuntu. Пакет только добавляет файлы, Node.js и nginx он не трогает,
а обновления безопасности будут приходить вместе с остальными пакетами.

```
sudo apt update
```
```
sudo apt install aspnetcore-runtime-10.0
```
```
dotnet --list-runtimes
```

В списке должна быть строка `Microsoft.AspNetCore.App 10.`.

## 4. На сервере: файлы и пользователь

```
D=ДОМЕН
```
```
sudo useradd --system --home-dir /opt/cosmos-site --shell /usr/sbin/nologin cosmos-site
```
```
sudo mkdir -p /opt/cosmos-site/data
```
```
sudo tar -xzf ~/cosmos-site.tar.gz --no-same-owner -C /opt/cosmos-site
```
```
sudo chown cosmos-site:cosmos-site /opt/cosmos-site/data
```
```
sudo chmod 750 /opt/cosmos-site/data
```
```
sudo install -o cosmos-site -g cosmos-site -m 640 ~/pricing.json /opt/cosmos-site/data/pricing.json
```
```
sudo install -o cosmos-site -g cosmos-site -m 640 ~/legal.json /opt/cosmos-site/data/legal.json
```

Переменная `D` живёт до выхода из SSH: после нового входа задайте её снова.

## 5. На сервере: служба

```
sudo cp /opt/cosmos-site/deploy/cosmos-site.service /etc/systemd/system/
```
```
sudo systemctl daemon-reload
```
```
sudo systemctl enable --now cosmos-site
```
```
systemctl status cosmos-site --no-pager
```

Ожидается `active (running)`. Проверьте сайт в обход nginx, снаружи его пока не видно:

```
curl -s http://127.0.0.1:8138/web/health
```

Ожидается `{"ok":true,"mode":"site-only","version":"1.0.0"}`.

## 6. На сервере: сертификат

Проверьте файлы, как велит раздел 8 `SERVER-SETUP.md`: `issuer` сертификата должен совпасть с `subject` первого блока
из второй команды.

```
openssl x509 -in ~/certificate.crt -noout -issuer
```
```
openssl crl2pkcs7 -nocrl -certfile ~/certificate_ca.crt | openssl pkcs7 -print_certs -noout
```

Установите:

```
sudo mkdir -p /etc/nginx/ssl/$D
```
```
{ awk 1 ~/certificate.crt; awk '1; /END CERTIFICATE/ {exit}' ~/certificate_ca.crt; } | sudo tee /etc/nginx/ssl/$D/fullchain.crt > /dev/null
```
```
openssl crl2pkcs7 -nocrl -certfile /etc/nginx/ssl/$D/fullchain.crt | openssl pkcs7 -print_certs -noout
```
```
sudo install -m 600 ~/certificate.key /etc/nginx/ssl/$D/private.key
```
```
rm ~/certificate.key
```
```
openssl x509 -in /etc/nginx/ssl/$D/fullchain.crt -noout -ext subjectAltName
```

Третья команда должна показать два сертификата: домен и промежуточный. Последняя показывает имена, которые
покрывает сертификат.

## 7. На сервере: новый домен в nginx

Это единственный шаг, затрагивающий общий nginx. Создаётся новый файл, а файл `volleysigns` не меняется.

```
sudo cp /opt/cosmos-site/deploy/nginx-cosmos-site-https.conf /etc/nginx/sites-available/cosmos-site
```
```
sudo sed -i "s/example\.com/$D/g" /etc/nginx/sites-available/cosmos-site
```

**Если сертификат не покрывает `www`:** откройте файл (`sudo nano /etc/nginx/sites-available/cosmos-site`), удалите
второй блок `server` и уберите `www.ДОМЕН` из первого.

```
sudo ln -s /etc/nginx/sites-available/cosmos-site /etc/nginx/sites-enabled/cosmos-site
```
```
sudo nginx -t
```

**Только если `nginx -t` сказал `test is successful`:**

```
sudo systemctl reload nginx
```

Если `nginx -t` показал ошибку, reload не делайте: nginx продолжит работать на старых настройках, и старый сайт ничего
не заметит. Уберите ссылку (`sudo rm /etc/nginx/sites-enabled/cosmos-site`), поправьте файл и повторите.

Заглушку `default-drop` повторно не ставить, а файрвол и `restart` nginx не нужны.

## 8. Проверить

Новый сайт:

```
curl -I http://$D
```

Ожидается `301` на `https://`.

```
curl -I https://$D
```

Ожидается `HTTP/2 200`.

```
openssl s_client -connect 127.0.0.1:443 -servername $D -verify_hostname $D < /dev/null 2>/dev/null | grep "Verify return code"
```

Ожидается `Verify return code: 0 (ok)`.

```
curl -s -o /dev/null -w '%{http_code}\n' https://$D/api/ping
```

Ожидается `404`: сервер синхронизации закрыт.

Старый сайт. Ответ должен совпасть с тем, что был в разделе 1:

```
curl -I https://СТАРЫЙ_ДОМЕН
```
```
systemctl is-active volleysigns
```

Откройте новый домен в браузере и посмотрите страницу, цены, подвал и ролики. Затем приберите за собой:

```
rm ~/cosmos-site.tar.gz ~/pricing.json ~/legal.json ~/certificate.crt ~/certificate_ca.crt
```

Допишите проект в таблицу портов раздела 1 `SERVER-SETUP.md`:

| Проект | Порт | Служба | Папка | Сайт nginx |
|---|---|---|---|---|
| Космос календарь: сайт | 8138 | `cosmos-site` | `/opt/cosmos-site` | `/etc/nginx/sites-available/cosmos-site` |

## Если что-то не так

| Что видно | Что делать |
|---|---|
| Служба не `active` | `sudo journalctl -u cosmos-site -n 50 --no-pager` |
| `226/NAMESPACE` в статусе | нет папки `/opt/cosmos-site/data` |
| `127.0.0.1:8138` отвечает, а домен нет | проверить A-запись и `ls -l /etc/nginx/sites-enabled` |
| Домен отвечает обрывом связи | запрос попал в `default-drop`: `server_name` не совпал с доменом |
| Android или программы ругаются на сертификат | в `fullchain.crt` нет промежуточного сертификата |
| Нет подвала | нет `legal.json` в `/opt/cosmos-site/data` или в нём пустые поля |
| Цены «появится позже» | нет `pricing.json` в `/opt/cosmos-site/data` |

## Обновить сайт

На ПК выполните команды раздела 2 для архива, затем на сервере:

```
sudo tar -xzf ~/cosmos-site.tar.gz --no-same-owner -C /opt/cosmos-site
```
```
sudo systemctl restart cosmos-site
```
```
systemctl status cosmos-site --no-pager
```

Папка `data` при этом не меняется: в архиве её нет.

Цены и реквизиты меняются без обновления: положите новый файл той же командой `sudo install` из раздела 4,
перезапуск не нужен.

## Как убрать сайт

Старый сайт это не затрагивает.

```
sudo rm /etc/nginx/sites-enabled/cosmos-site
```
```
sudo nginx -t
```
```
sudo systemctl reload nginx
```
```
sudo systemctl disable --now cosmos-site
```

После этого домен отвечает обрывом связи (`default-drop`), а файлы остаются в `/opt/cosmos-site`.

## Что не настроено

Как и у VolleySigns, продление сертификата вручную: не позже срока (`openssl x509 -in /etc/nginx/ssl/$D/fullchain.crt
-noout -enddate`) пройдите раздел 6 с новыми файлами. Срок новых сертификатов — не больше 200 дней, а с 15.03.2027 — не
больше 100.
