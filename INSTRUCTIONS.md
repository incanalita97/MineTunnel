# 🎮 MineTunnel Complete Guide: Direct Peer-to-Peer (P2P) Minecraft

> **Zero Accounts • Zero Third-Party Servers • Pure UDP STUN Hole Punching • Bypasses PLDT Home Fiber CGNAT**

MineTunnel is designed to support **every Java Minecraft server software and version**, including **Vanilla**, **Paper**, **Purpur**, **Spigot**, **Fabric**, **Forge**, **NeoForge**, **BungeeCord**, **Velocity**, and **Singleplayer "Open to LAN" worlds** (from Minecraft 1.7 all the way to 1.21+).

---

## ⚡ Why Direct Peer-to-Peer (P2P) Solves PLDT CGNAT

If you have **PLDT Home Fiber (or Converge / Globe)** in the Philippines:
- **The CGNAT Barrier**: PLDT assigns home routers an internal private WAN IP (`100.64.0.0/10`). Traditional inbound port forwarding in `192.168.1.1` fails because incoming connection attempts are blocked upstream at PLDT's core exchanges.
- **Why P2P STUN Hole Punching Works**:
  1. Both your PC and your friend's PC send an outbound UDP query to a public RFC 5389 STUN server (such as Google STUN: `stun.l.google.com:19302`).
  2. The STUN server discovers each computer's external public IP and mapped NAT port.
  3. MineTunnel generates a compact **P2P Invite Code** (e.g. `MINE-P2P#112.198.45.12:49152#SteveWorld`).
  4. Both MineTunnel instances send synchronized UDP punch packets directly towards each other.
  5. Once the NAT pinhole opens, your computers communicate **directly peer-to-peer over the internet**.
  6. **Zero accounts to create, zero credit cards, zero payment verification, and the lowest possible latency (~10–25ms ping)!**

---

## 🚀 Part 1: Host Setup (The Player Running the Server)

1. **Start Your Minecraft Server**:
   - Run your Java server (`server.jar`, `run.bat` for Forge/Fabric, or `paper.jar`) on port **25565**, **OR** load your singleplayer world and click **ESC** -> **Open to LAN**.
2. **Launch MineTunnel**:
   - Open **`MineTunnel.exe`**.
   - MineTunnel will automatically detect your local Minecraft game: **`● Java Online (:25565)`** (or active dynamic LAN port).
3. **Start the P2P Host**:
   - In the **👑 Host Game** tab, enter your world name (e.g. `SteveWorld`).
   - Click **`▶ Start P2P Host`**.
   - MineTunnel will query Google STUN, resolve your external endpoint, and generate your P2P Invite Code:
     ```text
     MINE-P2P#175.158.241.115:16948#SteveWorld#AgE3n9Zf01aC44
     ```
4. **Share with Friends**:
   - Click **`📋 Copy Invite Code`** to copy the code to your clipboard (with instant **✓ Copied to Clipboard!** feedback).
   - Send the code to your friends over Discord or Messenger!

---

## 🎮 Part 2: Friend Setup (Joining Your World)

1. Friend opens **`MineTunnel.exe`**.
2. Go to the **🎮 Join Game** tab.
3. Click **`📋 Paste from Clipboard`** (automatically pastes and validates the invite code).
4. Click **`⚡ Connect to Friend`**.
5. Within 1–2 seconds, the status badge turns:
   ```text
   🟢 Connected to SteveWorld (Ping: 18 ms)
   ```
6. **Connect in Minecraft**:
   - Friend opens **Minecraft: Java Edition** (same version as Host).
   - Click **Multiplayer**.
   - **Zero-Config Option**: The world will automatically appear under **LAN Games** at the bottom of the multiplayer list!
   - **Direct Connection Option**: Click **Direct Connection**, click **`📋 Copy Address`** in MineTunnel, and paste:
     ```text
     127.0.0.1:25565
     ```
   - Click **Join Server**!
   - Data streams directly between your computers peer-to-peer!

---

## 🛠️ Essential Minecraft Settings & Troubleshooting

### 1. Allowing Non-Premium / "Cracked" Friends to Join
If any of your friends use launchers like TLauncher, Prism (offline), or TL:
1. Open your server folder and open `server.properties` with Notepad.
2. Change:
   ```properties
   online-mode=false
   ```
3. Restart your Minecraft server.

### 2. Windows Defender Firewall Prompt
When starting Minecraft or MineTunnel for the first time, Windows Defender Firewall may display a pop-up:
- Make sure to check **Private networks** (and Public networks if on a laptop).
- Click **Allow access**.

### 3. Modded Minecraft (Forge / Fabric / NeoForge)
- All players must have the **exact same modpack** and mod versions installed on their client.
- Client-side mods (e.g. OptiFine, Sodium, Minimap) do not need to be on the server.
- The tunnel supports full Forge chunk and entity packet streaming with zero configuration.

### 4. Symmetric NAT (Cellular / Mobile Hotspots)
If one player is tethering from mobile data with strict symmetric NAT, UDP hole punching may take a few attempts:
- Simply restart the P2P Host and have the client click **Connect P2P** again to try a fresh pinhole.

---

## ⚙️ Alternative Option: Custom Relay Mode

If you have a dedicated Linux/Windows VPS on AWS, DigitalOcean, or Linode:
1. Run `MineTunnelRelay.exe` or `python3 Relay/minetunnel_relay.py 25560` on your VPS.
2. In MineTunnel, switch to **`⚙️ Custom Relay Mode`**.
3. Enter your VPS IP:Port and Room Name.
4. Click **Start Hosting via Relay**.
