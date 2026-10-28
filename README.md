# MineTunnel

A lightweight peer-to-peer (P2P) tunnel for playing Minecraft LAN worlds and servers with friends over the internet without port forwarding.

I built this because playing Minecraft with friends in the Philippines (especially on PLDT Home Fiber, Converge, or Globe) is usually a headache. Most residential internet connections are placed behind **Carrier-Grade NAT (CGNAT)**, which means your router doesn't get a public IPv4 address and standard port forwarding (port 25565) doesn't work.

Traditional workarounds like Hamachi or Radmin VPN require installing third-party virtual network adapters, creating accounts, and often suffer from high ping or connection drops. MineTunnel solves this using direct **UDP hole punching via STUN**, creating a direct connection between you and your friend's PC—no VPN adapters, no accounts, and no middleman relay server needed.

---

## Features

- **Direct P2P**: Direct connection between players with minimal latency (usually 10–30ms on local ISPs).
- **PLDT & Converge CGNAT Bypass**: Uses RFC 5389 STUN hole punching to traverse residential NATs without needing a public IP or port forwarding.
- **Auto LAN Discovery**: Automatically detects running local Minecraft servers or singleplayer LAN worlds and broadcasts them to connected friends so they show up under **LAN Games**.
- **Dynamic LAN Port Forwarding**: If you open a singleplayer world to LAN, Minecraft assigns a random port (e.g. 54321). MineTunnel automatically detects and routes traffic to that port without restarting.
- **End-to-End Encrypted**: All tunnel traffic is encrypted with AES-256 and authenticated with HMAC-SHA256.
- **Zero Dependencies**: Pure C# (.NET Framework 4.8) with no external packages, runtimes, or accounts needed. Runs on any modern Windows 10 or 11 system out of the box.
- **Optional Relay Mode**: If one player is on mobile hotspot or strict symmetric NAT where direct UDP hole punching isn't possible, you can switch to Relay mode using your own VPS.

---

## How to Use

### 1. Download
Download `MineTunnelSetup.exe` (installer) or `MineTunnel.exe` (standalone portable) from the [Releases](https://github.com/incanalita97/MineTunnel/releases) page.

### 2. Hosting a Game
1. Open your Minecraft world:
   - In Singleplayer: Press **ESC** -> **Open to LAN**.
   - Or run your dedicated server (`server.jar` on port 25565).
2. Open `MineTunnel.exe`. It will automatically scan and detect your Minecraft game.
3. In the **Host Game** tab, pick a room name and click **Start P2P Host**.
4. Click **Copy Invite Code** and send it to your friend (via Discord, Messenger, etc.).

### 3. Joining a Game
1. Open `MineTunnel.exe` and go to the **Join Game** tab.
2. Enter your in-game name, paste your friend's invite code (or click **Paste from Clipboard**), and click **Connect to Friend**.
3. Open Minecraft: Java Edition:
   - Go to **Multiplayer**. The host's world will usually appear automatically under **LAN Games**.
   - If it doesn't appear, click **Direct Connection**, enter `127.0.0.1:25565`, and click **Join Server**.

---

## How it Works

1. **STUN Resolution**: When you start hosting or connecting, MineTunnel contacts a public STUN server (such as Google's `stun.l.google.com:19302`) over UDP to discover your external public IP and NAT-mapped port.
2. **Invite Code**: The host's coordinates and a random cryptographic master secret are packaged into a base64 invite code (e.g. `MINE-P2P#...`).
3. **Simultaneous UDP Hole Punching**: When the joiner enters the code, both PCs send UDP packets to each other's mapped endpoints at the same time. This creates outbound state entries in both NAT routers, allowing subsequent packets to pass through the CGNAT firewall directly.
4. **Reliable Transport & Crypto**: Once the hole is punched, MineTunnel runs a lightweight reliable UDP protocol (RUDP) with sequence tracking, ACKs, packet retransmission, AES-256-CBC encryption, and HMAC-SHA256 authentication.
5. **Local Proxy**: On the joiner's machine, MineTunnel listens on `127.0.0.1:25565` and pipes the Minecraft TCP stream through the encrypted UDP tunnel directly to the host's Minecraft server.

---

## Common Issues & Troubleshooting

- **Windows Firewall**: When you run MineTunnel or Minecraft for the first time, Windows Defender Firewall will ask for network permissions. Make sure to check **Private networks** (and Public networks if on Wi-Fi) and click **Allow access**. If blocked, UDP hole punch packets will be dropped by Windows.
- **Offline / Non-Premium ("Cracked") Launchers**: If you or your friends use launchers like TLauncher or Prism in offline mode, make sure `online-mode=false` in your server's `server.properties`. For singleplayer LAN worlds, Minecraft enforces online UUIDs by default; you can use a lightweight fabric mod like *Offline LAN* if needed.
- **Mobile Data / Hotspots (Symmetric NAT)**: Fiber connections (PLDT Home Fiber, Converge, Globe Fiber) generally use Cone NAT, which works great with UDP hole punching. Mobile hotspots (Globe/Smart 4G/5G) often use strict Symmetric NAT where external ports change randomly per destination. If hole punching fails on mobile data, use the **Relay & Advanced** tab with a cheap VPS.
- **Modded Minecraft**: Forge, Fabric, NeoForge, and Quilt all work over MineTunnel. Just make sure all players have the exact same mods and modpack versions installed.

---

## Building from Source

MineTunnel uses the built-in Microsoft C# compiler (`csc.exe`) included with Windows and .NET Framework 4.8. You don't need Visual Studio or any NuGet packages to build it.

```cmd
git clone https://github.com/incanalita97/MineTunnel.git
cd MineTunnel
build.bat
```

This will produce the following in `bin/`:
- `MineTunnel.exe` — Standalone portable application
- `MineTunnelSetup.exe` — Windows installer
- `MineTunnelRelay.exe` — Optional standalone relay server
- `TestRunner.exe` — Integration test runner
- `NetworkResilienceTest.exe` — Resilience and security test suite

To run the automated tests:
```cmd
bin\TestRunner.exe
bin\NetworkResilienceTest.exe
powershell -ExecutionPolicy Bypass -File .\test_suite.ps1
```

---

## Project Structure

```
MineTunnel/
├── Common/            # Minecraft SLP ping protocol, packet framing, and STUN helpers
├── Core/              # UDP hole puncher, RUDP transport, AES crypto, and Minecraft detector
├── UI/                # Windows Forms dark theme interface
├── Installer/         # Self-extracting Windows installer wizard
├── Relay/             # Fallback relay server (C# and standalone Python script)
├── .github/workflows/ # GitHub Actions CI build & test workflow
├── build.bat          # 1-click build script using csc.exe
├── test_suite.ps1     # Automated test suite
└── LICENSE            # MIT License
```

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
