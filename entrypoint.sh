#!/bin/sh
systemctl enable tor
systemctl start tor

systemctl enable privoxy
systemctl start privoxy

systemctl enable jacred
systemctl start jacred

crond -f -l 8