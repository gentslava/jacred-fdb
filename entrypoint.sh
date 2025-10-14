#!/bin/sh
dotnet JacRed.dll || exit 1

privoxy--no-daemon /etc/privoxy/config

crond -f -l 8
