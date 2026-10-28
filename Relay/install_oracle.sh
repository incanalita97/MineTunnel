#!/usr/bin/env bash
# ==============================================================================
# MineTunnel Relay 1-Click Setup for Oracle Cloud Free Tier (Linux)
# Compatible with: Oracle Linux 8/9, Ubuntu 20.04/22.04/24.04 LTS, Debian
# ==============================================================================

set -e

echo "=========================================================="
echo "   ⛏ Setting up MineTunnel Relay on Oracle Cloud VM"
echo "=========================================================="

if [ "$EUID" -ne 0 ]; then
  echo "[!] Please run with sudo or as root: sudo bash install_oracle.sh"
  exit 1
fi

PORT=25560
INSTALL_DIR="/opt/minetunnel-relay"

echo "[1/4] Ensuring Python 3 is installed..."
if ! command -v python3 &> /dev/null; then
    if command -v apt-get &> /dev/null; then
        apt-get update -y && apt-get install -y python3
    elif command -v dnf &> /dev/null; then
        dnf install -y python3
    elif command -v yum &> /dev/null; then
        yum install -y python3
    fi
fi

echo "[2/4] Installing MineTunnel Relay script to $INSTALL_DIR..."
mkdir -p "$INSTALL_DIR"
cp minetunnel_relay.py "$INSTALL_DIR/minetunnel_relay.py" 2>/dev/null || cat << 'EOF' > "$INSTALL_DIR/minetunnel_relay.py"
#!/usr/bin/env python3
import sys, os, struct, socket, threading, time

MAGIC = 0x4D43
PACKET_ROOM_AUTH_REQ = 0x01
PACKET_ROOM_AUTH_RESP = 0x02
PACKET_LAN_BROADCAST = 0x03
PACKET_CHANNEL_OPEN = 0x04
PACKET_CHANNEL_DATA = 0x05
PACKET_CHANNEL_CLOSE = 0x06
PACKET_PING = 0x07
PACKET_PONG = 0x08
PACKET_RELAY_REGISTER_HOST = 0x20
PACKET_RELAY_REGISTER_RESP = 0x21
PACKET_RELAY_JOIN_CLIENT = 0x22
PACKET_RELAY_JOIN_RESP = 0x23

def read_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk: return None
        buf.extend(chunk)
    return bytes(buf)

def read_packet(sock):
    header = read_exact(sock, 7)
    if not header: return None
    magic, ptype, plen = struct.unpack('>HBI', header)
    if magic != MAGIC: raise ValueError("Invalid packet magic")
    payload = read_exact(sock, plen) if plen > 0 else b''
    return ptype, payload

def write_packet(sock, ptype, payload=b''):
    header = struct.pack('>HBI', MAGIC, ptype, len(payload))
    sock.sendall(header + payload)

def read_dotnet_string(data, offset):
    length, shift = 0, 0
    while offset < len(data):
        b = data[offset]; offset += 1
        length |= (b & 0x7F) << shift
        if (b & 0x80) == 0: break
        shift += 7
    s = data[offset:offset+length].decode('utf-8', errors='replace')
    return s, offset + length

def write_dotnet_string(s):
    encoded = s.encode('utf-8')
    res, length = bytearray(), len(encoded)
    while length >= 0x80:
        res.append((length | 0x80) & 0xFF); length >>= 7
    res.append(length & 0xFF); res.extend(encoded)
    return bytes(res)

class RelayRoom:
    def __init__(self, name, password, host_sock, host_addr):
        self.name = name; self.password = password
        self.host_sock = host_sock; self.host_addr = host_addr
        self.host_lock = threading.Lock()
        self.clients = {}; self.clients_lock = threading.Lock()

class MineTunnelRelay:
    def __init__(self, host="0.0.0.0", port=25560):
        self.host, self.port = host, port
        self.rooms, self.rooms_lock = {}, threading.Lock()
        self.next_client_id, self.id_lock = 100, threading.Lock()
        self.is_running = True

    def start(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((self.host, self.port))
        sock.listen(100)
        print("[Relay] Listening on {}:{} (TCP)...".format(self.host, self.port))
        while self.is_running:
            client_sock, addr = sock.accept()
            client_sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            t = threading.Thread(target=self.handle_initial, args=(client_sock, addr))
            t.daemon = True; t.start()

    def handle_initial(self, sock, addr):
        try:
            packet = read_packet(sock)
            if not packet: sock.close(); return
            ptype, payload = packet
            if ptype == PACKET_RELAY_REGISTER_HOST:
                self.handle_host_register(sock, addr, payload)
            elif ptype in (PACKET_RELAY_JOIN_CLIENT, PACKET_ROOM_AUTH_REQ):
                self.handle_client_join(sock, addr, payload)
            else: sock.close()
        except Exception:
            try: sock.close()
            except Exception: pass

    def handle_host_register(self, sock, addr, payload):
        room_name, off = read_dotnet_string(payload, 0)
        password, _ = read_dotnet_string(payload, off)
        with self.rooms_lock:
            if self.rooms.get(room_name.lower()):
                fail = struct.pack('?', False) + write_dotnet_string("Room active.")
                write_packet(sock, PACKET_RELAY_REGISTER_RESP, fail); sock.close(); return
            room = RelayRoom(room_name, password, sock, addr)
            self.rooms[room_name.lower()] = room
        ok = struct.pack('?', True) + write_dotnet_string("Room registered.")
        write_packet(sock, PACKET_RELAY_REGISTER_RESP, ok)
        print("[Relay] Host registered Room '{}' from {}".format(room_name, addr))
        try:
            while self.is_running:
                p = read_packet(sock)
                if not p: break
                ptype, pdata = p
                with room.clients_lock:
                    for cid, (csock, clock, _) in list(room.clients.items()):
                        try:
                            with clock: write_packet(csock, ptype, pdata)
                        except Exception: pass
        finally:
            with self.rooms_lock: self.rooms.pop(room_name.lower(), None)
            print("[Relay] Host disconnected from '{}'".format(room_name))
            try: sock.close()
            except Exception: pass

    def handle_client_join(self, sock, addr, payload):
        room_name, off = read_dotnet_string(payload, 0)
        password, off2 = read_dotnet_string(payload, off)
        player_name = "Player"
        if off2 < len(payload): player_name, _ = read_dotnet_string(payload, off2)
        with self.rooms_lock: room = self.rooms.get(room_name.lower())
        if not room or room.password != password:
            fail = struct.pack('?i', False, 0) + write_dotnet_string("Invalid room or password.")
            write_packet(sock, PACKET_ROOM_AUTH_RESP, fail); sock.close(); return
        with self.id_lock:
            self.next_client_id += 1; cid = self.next_client_id
        ok = struct.pack('?i', True, cid) + write_dotnet_string("")
        write_packet(sock, PACKET_ROOM_AUTH_RESP, ok)
        with room.clients_lock: room.clients[cid] = (sock, threading.Lock(), player_name)
        print("[Relay] Player '{}' joined Room '{}' ({})".format(player_name, room_name, addr))
        try:
            while self.is_running:
                p = read_packet(sock)
                if not p: break
                with room.host_lock: write_packet(room.host_sock, p[0], p[1])
        finally:
            with room.clients_lock: room.clients.pop(cid, None)
            print("[Relay] Player '{}' left '{}'".format(player_name, room_name))
            try: sock.close()
            except Exception: pass

if __name__ == '__main__':
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 25560
    MineTunnelRelay(port=port).start()
EOF

chmod +x "$INSTALL_DIR/minetunnel_relay.py"

echo "[3/4] Creating systemd service (auto-start 24/7 on boot)..."
cat << 'EOF' > /etc/systemd/system/minetunnel-relay.service
[Unit]
Description=MineTunnel Relay Server for Oracle Cloud
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=/opt/minetunnel-relay
ExecStart=/usr/bin/python3 /opt/minetunnel-relay/minetunnel_relay.py 25560
Restart=always
RestartSec=5
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable minetunnel-relay
systemctl restart minetunnel-relay

echo "[4/4] Opening firewall port $PORT on Oracle Linux / Ubuntu..."
# 1. firewalld (Oracle Linux default)
if command -v firewall-cmd &> /dev/null && systemctl is-active --quiet firewalld; then
    firewall-cmd --zone=public --add-port=${PORT}/tcp --permanent
    firewall-cmd --reload
    echo "  [OK] Port $PORT opened via firewalld."
fi

# 2. ufw (Ubuntu default)
if command -v ufw &> /dev/null && ufw status | grep -q "Status: active"; then
    ufw allow ${PORT}/tcp
    echo "  [OK] Port $PORT opened via ufw."
fi

# 3. iptables (Oracle Cloud default iptables chains)
if command -v iptables &> /dev/null; then
    iptables -I INPUT 6 -p tcp --dport ${PORT} -m state --state NEW,ESTABLISHED -j ACCEPT 2>/dev/null || \
    iptables -I INPUT 1 -p tcp --dport ${PORT} -j ACCEPT
    if command -v netfilter-persistent &> /dev/null; then
        netfilter-persistent save 2>/dev/null || true
    fi
    echo "  [OK] Port $PORT opened via iptables."
fi

PUBLIC_IP=$(curl -s -4 ifconfig.me || curl -s -4 icanhazip.com || echo "<your-oracle-ip>")

echo ""
echo "=========================================================="
echo "  🎉 SUCCESS! MineTunnel Relay is Active & Running 24/7!"
echo "=========================================================="
echo "  Oracle Public IP : $PUBLIC_IP"
echo "  Relay Port       : $PORT"
echo ""
echo "  Next Steps in MineTunnel (on your PC):"
echo "  1. Open MineTunnel.exe on your Windows PC."
echo "  2. In 'Host Room', enter Oracle VM Public IP: $PUBLIC_IP"
echo "  3. Click '▶ Start Hosting via Oracle Relay'!"
echo "=========================================================="
