# Установка

#### Linux
```bash
curl -s https://raw.githubusercontent.com/immisterio/jacred-fdb/main/install.sh | bash
```

### Docker & Docker Compose

```bash
docker run --rm -d --privileged --name jacred -p 9117:9117 gentslava/jacred:latest
```

Для работы в режиме постоянного хранения просто подключите том к контейнеру, добавив `-v ~/jacred/Data:/home/jacred/Data`, где путь к папке `~/jacred/Data` — это базовый путь, можно указать свой. Пример команды:

```bash
docker run --rm -d --privileged --name jacred -v ~/jacred/Data:/home/jacred/Data -p 9117:9117 gentslava/jacred:latest
```

Вы можете указать путь до init.conf файла, где определены настройки работы приложения, добавив `-v ~/jacred/init.conf:/home/jacred/init.conf`. Пример команды:

```bash
docker run --rm -d --privileged --name jacred -v ~/jacred/init.conf:/home/jacred/init.conf -v ~/jacred/Data:/home/jacred/Data -p 9117:9117 gentslava/jacred:latest
```

#### Docker Compose

```yml
# docker-compose.yml

version: '3.3'
services:
    jacred:
        image: gentslava/jacred
        container_name: jacred
        volumes:
            - ~/jacred/init.conf:/home/jacred/init.conf
            - ~/jacred/Data:/home/jacred/Data
        ports:
            - 9117:9117
        restart: unless-stopped
        privileged: true

```

# Настройка парсера
1. Настроить init.conf (пример настроек в example.conf)
2. Перенести в crontab "Data/crontab" или указать сервер "syncapi" в init.conf

# Источники
Kinozal, Nnmclub, Rutor, Torrentby, Bitru, Rutracker, Megapeer, Selezen, Toloka (UKR), Rezka, Baibako, LostFilm, Anilibria, Animelayer, Anifilm

# Доступ к доменам .onion
1. Запустить tor на порту 9050
2. В init.conf указать .onion домен в host

# Параметры init.conf
* apikey - включение авторизации по ключу
* mergeduplicates - объединять дубликаты в выдаче
* openstats - открыть доступ к статистике
* opensync - разрешить синхронизацию с базой через syncapi
* syncapi - источник с открытым opensync для синхронизации базы
* timeSync - интервал синхронизации с базой syncapi
* maxreadfile - максимальное количество открытых файлов за один поисковый запрос
* evercache - хранить открытые файлы в кеше (рекомендуется для общего доступа с высокой нагрузкой)
* fdbPathLevels - для релиза 25.01.2023 установить в 1
* timeStatsUpdate - интервал обновления статистики в минутах


# Настройка init.conf
* Список всех параметров, а так же значения по умолчанию смотреть в example.conf
* В init.conf нужно указывать только те параметры, которые хотите изменить

```
{
  "listenport": 9120, // изменили порт
  "NNMClub": {        // изменили домен на адрес из сети tor
    "alias": "http://nnmclub2vvjqzjne6q4rrozkkkdmlvnrcsyes2bbkm7e5ut2aproy4id.onion"
  },
  "globalproxy": [
    {
      "pattern": "\\.onion",  // запросы на домены .onion отправить через прокси
      "list": [
        "socks5://127.0.0.1:9050" // прокси сервер tor
      ]
    }
  ]
}
```
