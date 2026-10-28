# ⛏ MineTunnel

> **Peer-to-Peer (P2P) Minecraft Tunnel & LAN Bridge tailored for Windows 10 and Windows 11 with PLDT CGNAT bypass.**

---

## 🌟 Why MineTunnel?

Playing Minecraft multiplayer with friends over the internet in the Philippines on **PLDT Home Fiber (or Converge/Globe)** usually fails because of **Carrier-Grade NAT (CGNAT)** and blocked incoming ports on home routers.

**MineTunnel** eliminates the need for third-party VPN accounts, credit cards, and relay servers using **pure Peer-to-Peer (P2P) UDP STUN Hole Punching**:
- **⚡ Direct Peer-to-Peer (P2P)**: Direct computer-to-computer connection between you and your friend. Lowest possible latency (~10–25ms ping).
- **🔒 Zero Accounts & Zero Payment**: No Oracle Cloud, no Localtonet, no Hamachi logins. No credit card or billing verification ever.
- **🌐 RFC 5389 STUN NAT Traversal**: Queries public STUN servers (e.g. Google STUN: `stun.l.google.com:19302`) to resolve external mapped NAT endpoints and punches direct pinholes through PLDT CGNAT.
- **📋 1-Click P2P Invite Code**: Generates a compact shareable connection token (e.g. `MINE-P2P#112.198.45.12:49152#SteveWorld`).
- **🔍 Real-Time Minecraft Detection**: Built-in Server List Ping (SLP) protocol monitor that detects your local Minecraft server or LAN world, displaying the MOTD, player count, and version in real-time.
- **🎮 Effortless Minecraft Connection**: When friends connect via P2P, MineTunnel opens a local proxy on `127.0.0.1:25565`. Friends join in Minecraft via Direct Connection to `127.0.0.1:25565`!
- **⚙️ Custom Relay Mode**: Built-in support for users who wish to host on their own private VPS.

---

## 🚀 Quick Start Guide

### For the Host (The Player Sharing the World on PLDT)

1. **Start Minecraft**:
   - Run your dedicated server (`server.jar` on port `25565`) or load a singleplayer world and click **ESC** -> **Open to LAN**.
2. **Launch MineTunnel**:
   - Open **`MineTunnel.exe`**.
   - MineTunnel will automatically detect your local Minecraft game.
   - Click **`▶ Start P2P Host`**.
   - Click **`📋 Copy P2P Invite Code for Friends`** and send the code to your friend.

---

## 🎮 For Friends (Joining the World)

1. Open **`MineTunnel.exe`** -> switch to **Join Server (P2P)** tab.
2. Paste the Host's P2P Invite Code into the input box.
3. Click **`⚡ Connect P2P to Friend`**.
4. Within 1–2 seconds, the connection is established!
5. Open **Minecraft: Java Edition** -> **Multiplayer** -> **Direct Connection**.
6. Enter `127.0.0.1:25565` and click **Join Server**!

---

## 📦 Installation & Executables

You have two ways to run MineTunnel:
1. **Windows Installer (`MineTunnelSetup.exe`)**:
   - Double-click `bin\MineTunnelSetup.exe`.
   - Choose your destination folder, enable Desktop / Start Menu shortcuts, and click **Install**.
   - Registers cleanly in Windows **"Installed Apps" / "Add or remove programs"** with an uninstaller.
2. **Standalone Executable (`MineTunnel.exe`)**:
   - Run `bin\MineTunnel.exe` directly with zero installation. Portable and USB-ready.

---

## 🛠️ Building from Source

MineTunnel requires **no third-party SDKs** or package managers. It builds using the Microsoft C# compiler (`csc.exe`) built into every modern Windows 10 and 11 installation:

1. Open Command Prompt or PowerShell in the project directory:
   ```cmd
   cd C:\Users\SPIRO\.gemini\antigravity\scratch\remote-admin
   build.bat
   ```
2. The output binaries will be created in `bin\`:
   - `bin\MineTunnel.exe` (Portable P2P Minecraft Tunnel application)
   - `bin\MineTunnelRelay.exe` (Zero-Port-Forward Relay for Windows)
   - `bin\MineTunnelSetup.exe` (Standalone Windows Installer)
   - `bin\TestRunner.exe` (Automated Integration Test Runner)

---

## 📂 Project Architecture

```
remote-admin/
│
├── Common/
│   ├── MinecraftProtocol.cs    # Minecraft SLP ping protocol, VarInt codec, LAN parser
│   └── TunnelPacket.cs         # Binary framing protocol with magic 0x4D43 ('MC')
│
├── Core/
│   ├── ConfigManager.cs        # Persistent settings storage (%AppData%\MineTunnel\settings.ini)
│   ├── StunClient.cs           # Pure C# RFC 5389/3489 STUN client for external NAT endpoint discovery
│   ├── P2PHolePuncher.cs       # Bidirectional UDP hole puncher, invite code generator & parser
│   ├── P2PStreamBridge.cs      # Streams Minecraft TCP game traffic across direct P2P UDP tunnel
│   ├── LanMulticastBridge.cs   # Listens to 224.0.2.60:4445 and rebroadcasts across tunnel
│   ├── MinecraftDetector.cs    # Actively monitors localhost for running MC instances
│   ├── RelayHostConnector.cs   # Outbound reverse tunnel connector for Custom Relay
│   ├── TunnelClient.cs         # Connects to relay, exposes local proxy port, syncs LAN
│   ├── TunnelHost.cs           # Host engine for room management
│   └── UpnpHelper.cs           # Local IP discovery & network helpers
│
├── Relay/
│   ├── minetunnel_relay.py     # Zero-dependency Python 3 relay daemon for Linux VPS
│   ├── RelayServer.cs          # Native C# multi-threaded relay engine
│   └── Program.cs              # Standalone Windows console relay server
│
├── UI/
│   ├── MineTunnelForm.cs       # Modern dark gaming UI with P2P host & join tools
│   └── Program.cs              # DPI awareness bootstrap (PerMonitorV2)
│
├── Installer/
│   ├── SetupForm.cs            # Standalone setup wizard & uninstaller
│   └── Program.cs              # Installer launcher
│
├── build.bat                   # 1-click build script using native csc.exe
├── INSTRUCTIONS.md             # Complete step-by-step setup guide for P2P & PLDT
├── TestRunner.cs               # End-to-end integration test suite
└── test_suite.ps1              # Automated PowerShell verification test suite
```
