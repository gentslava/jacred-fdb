ARG DOTNET_VERSION=9.0

# ### BUILD MAIN IMAGE START ###
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}

ENV JACRED_HOME=/home/jacred
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

RUN apt update && apt -y install curl systemd tor tor-geoipdb privoxy
COPY ./privoxy.config /etc/privoxy/config

COPY ./install.sh /
COPY ./update.sh /
COPY ./entrypoint.sh /

RUN sh /install.sh

RUN chmod +x /entrypoint.sh

WORKDIR /home/jacred

RUN crontab Data/crontab

EXPOSE 9117

VOLUME [ "/home/jacred/init.conf", "/home/jacred/Data" ]

ENTRYPOINT ["/lib/systemd/systemd"]
### BUILD MAIN IMAGE end ###