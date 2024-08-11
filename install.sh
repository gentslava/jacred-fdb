#!/usr/bin/bash
DEST="/home/jacred"

# Become root
# sudo su -
apt update && apt install -y wget unzip

# Install .NET
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh && chmod 755 dotnet-install.sh
./dotnet-install.sh --channel 6.0.1xx
echo "export DOTNET_ROOT=\$HOME/.dotnet" >> ~/.bashrc
echo "export PATH=\$PATH:\$HOME/.dotnet:\$HOME/.dotnet/tools" >> ~/.bashrc
source ~/.bashrc

# Download zip
mkdir $DEST -p && cd $DEST
wget https://github.com/immisterio/jacred-fdb/releases/latest/download/publish.zip
unzip -oq publish.zip
rm -f publish.zip

# Download database
wget http://redb.cfhttp.top/latest.zip
echo "Unpacking the database"
unzip -oq latest.zip
rm -f latest.zip

# Create service
echo ""
echo "Install service to /etc/systemd/system/jacred.service ..."
touch /etc/systemd/system/jacred.service && chmod 664 /etc/systemd/system/jacred.service
cat <<EOF > /etc/systemd/system/jacred.service
[Unit]
Description=jacred
Wants=network.target
After=network.target
[Service]
WorkingDirectory=$DEST
ExecStart=$HOME/.dotnet/dotnet JacRed.dll
#ExecReload=/bin/kill -s HUP $MAINPID
#ExecStop=/bin/kill -s QUIT $MAINPID
Restart=always
[Install]
WantedBy=multi-user.target
EOF

# Enable service
systemctl daemon-reload
systemctl enable jacred
systemctl start jacred

crontab -l | { cat; echo "*/40 * * * * curl -s \"http://127.0.0.1:9117/jsondb/save\""; } | crontab -

# iptables drop
cat <<EOF > iptables-drop.sh
#!/bin/sh
echo "Stopping firewall and allowing everyone..."
iptables -F
iptables -X
iptables -t nat -F
iptables -t nat -X
iptables -t mangle -F
iptables -t mangle -X
iptables -P INPUT ACCEPT
iptables -P FORWARD ACCEPT
iptables -P OUTPUT ACCEPT
EOF

# Note
echo ""
echo "################################################################"
echo ""
echo "Have fun!"
echo ""
echo "Please check/edit $DEST/init.conf params and configure it"
echo ""
echo "Then [re]start it as systemctl [re]start jacred"
echo ""
echo "Clear iptables if port 9117 is not available"
echo "bash $DEST/iptables-drop.sh"
echo ""
echo "Full setup crontab"
echo "crontab $DEST/Data/crontab"
echo ""
