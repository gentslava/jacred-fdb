# ### BUILD MAIN IMAGE START ###
FROM ubuntu

RUN apt update && apt -y install curl systemd tor tor-geoipdb privoxy
COPY ./privoxy.config /etc/privoxy/config

COPY ./install.sh /
COPY ./update.sh /

RUN sh /install.sh

WORKDIR /home/jacred

RUN crontab Data/crontab

EXPOSE 9117

VOLUME [ "/home/jacred/init.conf", "/home/jacred/Data" ]

ENTRYPOINT ["/lib/systemd/systemd"]
### BUILD MAIN IMAGE end ###