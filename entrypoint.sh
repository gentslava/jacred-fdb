#!/bin/sh
dotnet JacRed.dll || exit 1

privoxy--no-daemon /etc/privoxy/config

crontab /etc/cron.d/jacred
crond -f -l 8
