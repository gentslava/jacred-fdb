ARG DOTNET_VERSION=9.0

### BUILD JACRED MULTIARCH START ###
FROM --platform=$BUILDPLATFORM alpine AS builder

WORKDIR /app

# Get and unpack JacRed
RUN apk --no-cache --update add bash wget unzip
RUN wget https://github.com/immisterio/jacred-fdb/releases/latest/download/publish.zip
RUN unzip -o publish.zip
RUN rm -f publish.zip
### BUILD JACRED MULTIARCH END ###

# ### BUILD MAIN IMAGE START ###
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine

ENV JACRED_HOME=/home/jacred
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=builder /app $JACRED_HOME/

RUN apk --no-cache --update add icu-libs && \
    apk add --no-cache privoxy

COPY ./entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

COPY ./privoxy.config /etc/privoxy/config

COPY Data/crontab /etc/crontabs/root

WORKDIR $JACRED_HOME

EXPOSE 9117

HEALTHCHECK CMD wget --quiet --timeout=10 --spider http://127.0.0.1:9117 || exit 1

VOLUME [ "$JACRED_HOME" ]

ENTRYPOINT ["/entrypoint.sh"]
### BUILD MAIN IMAGE end ###