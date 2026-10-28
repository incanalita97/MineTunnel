#!/usr/bin/env python3
"""
MineTunnel Relay Server for Oracle Cloud Free Tier
Zero-Port-Forwarding & CGNAT Bypass Hub for Minecraft Java Edition
Runs on Python 3 (preinstalled on Oracle Linux 8/9 and Ubuntu 20/22/24 LTS)
"""

import sys
import os
import struct
import socket
import threading
import time

MAGIC = 0x4D43  # 'MC'

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
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)


def read_packet(sock):
    header = read_exact(sock, 7)
    if not header:
        return None
    magic, ptype, plen = struct.unpack('>HBI', header)
    if magic != MAGIC:
        raise ValueError("Invalid packet magic header: 0x{:04X}".format(magic))
    if plen > 16 * 1024 * 1024:
        raise ValueError("Packet length exceeds safety limit: {}".format(plen))
    payload = read_exact(sock, plen) if plen > 0 else b''
    if payload is None:
        return None
    return ptype, payload


def write_packet(sock, ptype, payload=b''):
    header = struct.pack('>HBI', MAGIC, ptype, len(payload))
    sock.sendall(header + payload)


def read_dotnet_string(data, offset):
    length = 0
    shift = 0
    while offset < len(data):
        b = data[offset]
        offset += 1
        length |= (b & 0x7F) << shift
        if (b & 0x80) == 0:
            break
        shift += 7
    s = data[offset:offset+length].decode('utf-8', errors='replace')
    return s, offset + length


def write_dotnet_string(s):
    encoded = s.encode('utf-8')
    res = bytearray()
    length = len(encoded)
    while length >= 0x80:
        res.append((length | 0x80) & 0xFF)
        length >>= 7
    res.append(length & 0xFF)
    res.extend(encoded)
    return bytes(res)


class RelayRoom:
    def __init__(self, name, password, host_sock, host_addr):
        self.name = name
        self.password = password
        self.host_sock = host_sock
        self.host_addr = host_addr
        self.host_lock = threading.Lock()
        self.clients = {}  # client_id -> (client_sock, client_lock, player_name)
        self.clients_lock = threading.Lock()
        self.created_at = time.time()


class MineTunnelRelay:
    def __init__(self, host="0.0.0.0", port=25560):
        self.host = host
        self.port = port
        self.rooms = {}
        self.rooms_lock = threading.Lock()
        self.next_client_id = 100
        self.id_lock = threading.Lock()
        self.is_running = True

    def start(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((self.host, self.port))
        sock.listen(100)
        print("=" * 60)
        print("  MineTunnel Relay Server for Oracle Cloud Free Tier")
        print("  Listening on {}:{} (TCP)".format(self.host, self.port))
        print("  Bypasses PLDT CGNAT with ~30ms latency from Philippines")
        print("=" * 60)
        print("[Relay] Ready for connections...\n")

        try:
            while self.is_running:
                client_sock, addr = sock.accept()
                client_sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                t = threading.Thread(target=self.handle_initial, args=(client_sock, addr))
                t.daemon = True
                t.start()
        except KeyboardInterrupt:
            print("\n[Relay] Shutting down...")
        finally:
            self.is_running = False
            sock.close()

    def handle_initial(self, sock, addr):
        try:
            packet = read_packet(sock)
            if not packet:
                sock.close()
                return

            ptype, payload = packet
            if ptype == PACKET_RELAY_REGISTER_HOST:
                self.handle_host_register(sock, addr, payload)
            elif ptype in (PACKET_RELAY_JOIN_CLIENT, PACKET_ROOM_AUTH_REQ):
                self.handle_client_join(sock, addr, payload)
            else:
                sock.close()
        except Exception as e:
            try:
                sock.close()
            except Exception:
                pass

    def handle_host_register(self, sock, addr, payload):
        room_name, off = read_dotnet_string(payload, 0)
        password, _ = read_dotnet_string(payload, off)

        if not room_name:
            fail_resp = struct.pack('?', False) + write_dotnet_string("Room name cannot be empty.")
            write_packet(sock, PACKET_RELAY_REGISTER_RESP, fail_resp)
            sock.close()
            return

        with self.rooms_lock:
            existing = self.rooms.get(room_name.lower())
            if existing:
                fail_resp = struct.pack('?', False) + write_dotnet_string("Room name is already active.")
                write_packet(sock, PACKET_RELAY_REGISTER_RESP, fail_resp)
                sock.close()
                return

            room = RelayRoom(room_name, password, sock, addr)
            self.rooms[room_name.lower()] = room

        ok_resp = struct.pack('?', True) + write_dotnet_string("Room registered successfully.")
        write_packet(sock, PACKET_RELAY_REGISTER_RESP, ok_resp)
        print("[Relay] Host registered Room '{}' from {}".format(room_name, addr))

        try:
            while self.is_running:
                p = read_packet(sock)
                if not p:
                    break
                ptype, pdata = p
                if ptype == PACKET_LAN_BROADCAST:
                    with room.clients_lock:
                        for cid, (csock, clock, cname) in list(room.clients.items()):
                            try:
                                with clock:
                                    write_packet(csock, PACKET_LAN_BROADCAST, pdata)
                            except Exception:
                                pass
                elif ptype in (PACKET_CHANNEL_DATA, PACKET_CHANNEL_CLOSE, PACKET_PONG):
                    with room.clients_lock:
                        for cid, (csock, clock, cname) in list(room.clients.items()):
                            try:
                                with clock:
                                    write_packet(csock, ptype, pdata)
                            except Exception:
                                pass
        except Exception:
            pass
        finally:
            with self.rooms_lock:
                self.rooms.pop(room_name.lower(), None)
            print("[Relay] Host disconnected from Room '{}'".format(room_name))
            try:
                sock.close()
            except Exception:
                pass

    def handle_client_join(self, sock, addr, payload):
        room_name, off = read_dotnet_string(payload, 0)
        password, off2 = read_dotnet_string(payload, off)
        player_name = "Player"
        if off2 < len(payload):
            player_name, _ = read_dotnet_string(payload, off2)

        with self.rooms_lock:
            room = self.rooms.get(room_name.lower())

        if not room:
            fail_resp = struct.pack('?i', False, 0) + write_dotnet_string("Room does not exist or Host is offline.")
            write_packet(sock, PACKET_ROOM_AUTH_RESP, fail_resp)
            sock.close()
            return

        if room.password != password:
            fail_resp = struct.pack('?i', False, 0) + write_dotnet_string("Invalid Room Password.")
            write_packet(sock, PACKET_ROOM_AUTH_RESP, fail_resp)
            sock.close()
            return

        with self.id_lock:
            self.next_client_id += 1
            client_id = self.next_client_id

        ok_resp = struct.pack('?i', True, client_id) + write_dotnet_string("")
        write_packet(sock, PACKET_ROOM_AUTH_RESP, ok_resp)

        client_lock = threading.Lock()
        with room.clients_lock:
            room.clients[client_id] = (sock, client_lock, player_name)

        print("[Relay] Player '{}' joined Room '{}' from {}".format(player_name, room_name, addr))

        try:
            while self.is_running:
                p = read_packet(sock)
                if not p:
                    break
                ptype, pdata = p
                with room.host_lock:
                    write_packet(room.host_sock, ptype, pdata)
        except Exception:
            pass
        finally:
            with room.clients_lock:
                room.clients.pop(client_id, None)
            print("[Relay] Player '{}' disconnected from Room '{}'".format(player_name, room_name))
            try:
                sock.close()
            except Exception:
                pass


if __name__ == '__main__':
    port = 25560
    if len(sys.argv) > 1:
        try:
            port = int(sys.argv[1])
        except ValueError:
            pass
    relay = MineTunnelRelay(port=port)
    relay.start()
