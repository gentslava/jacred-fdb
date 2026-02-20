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

* ПО УМОЛЧАНИЯ НАСТРОЕНА СИНХРОНИЗАЦИЯ БАЗЫ С ВНЕШНЕГО СЕРВЕРА

# Источники 
Kinozal, Nnmclub, Rutor, Torrentby, Bitru, Rutracker, Megapeer, Selezen, Toloka (UKR), Baibako, LostFilm, Anilibria, Animelayer

# Самостоятельный парсинг источников
1. Настроить init.conf (пример настроек в example.conf)
2. Перенести в crontab "Data/crontab" или указать сервер "syncapi" в init.conf

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
* timeStatsUpdate - интервал обновления статистики в минутах 


# Пример init.conf
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
example.conf

```
{
  "listenip": "any", // 127.0.0.1
  "listenport": 9117,
  "apikey": "",
  "mergeduplicates": true,
  "openstats": true,
  "opensync": false,
  "evercache": false,
  "timeStatsUpdate": 20,
  "timeSync": 10,
  "syncapi": "",
  "Rutor": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Megapeer": {
    "useproxy": false,
	"reqMinute": 14
  },
  "TorrentBy": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Bitru": {
    "useproxy": false,
	"reqMinute": 14
  },
  "NNMClub": {
    "useproxy": false,
	"reqMinute": 14,
	//"alias": "http://nnmclub2vvjqzjne6q4rrozkkkdmlvnrcsyes2bbkm7e5ut2aproy4id.onion"
  },
  "Anilibria": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Anifilm": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Rezka": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Kinozal": {
    "useproxy": false,
	"reqMinute": 14
	"login": {
      "u": "",
      "p": ""
    }
  },
  "Toloka": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Rutracker": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Selezen": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Animelayer": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Baibako": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Lostfilm": {
    "useproxy": false,
	"reqMinute": 14
    "cookie": ""
  },
  "proxy": {
    "useAuth": false,
    "BypassOnLocal": false,
    "username": "",
    "password": "",
    "list": [
      "ip:port",
      "socks5://ip:port"
	]
  },
  "globalproxy": [
	{
      "pattern": "\\.onion",
      "list": [
        "socks5://127.0.0.1:9050"
      ]
    }
  ]
}
```
