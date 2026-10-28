# MineTunnel Player Guide

This guide covers everything you need to know about setting up and playing Minecraft with friends using MineTunnel.

---

## Quick Setup

### If You Are the Host

1. **Start Minecraft**:
   - For singleplayer: Load your world, press **ESC**, and click **Open to LAN**.
   - For a server: Start your `server.jar` / Paper / Forge server as normal.
2. **Open MineTunnel**:
   - Run `MineTunnel.exe`. It will scan your machine and show your running Minecraft server.
3. **Start Hosting**:
   - Go to the **Host Game** tab.
   - Enter a room name and click **Start P2P Host**.
   - Click **Copy Invite Code** and send the code to your friends over Discord or Messenger.

### If You Are Joining a Friend

1. **Open MineTunnel**:
   - Run `MineTunnel.exe` and switch to the **Join Game** tab.
2. **Enter the Code**:
   - Paste the host's invite code (or click **Paste from Clipboard**).
   - Enter your in-game nickname and click **Connect to Friend**.
3. **Join in Minecraft**:
   - Open Minecraft (make sure you're using the same game version as the host).
   - Go to **Multiplayer**. The world should appear under **LAN Games**.
   - If it doesn't show up after a few seconds, click **Direct Connection**, enter `127.0.0.1:25565`, and click **Join Server**.

---

## Important Tips

### 1. Windows Firewall
When you open Minecraft or MineTunnel, Windows Defender Firewall might pop up asking if you want to allow the app on your network:
- Check **Private networks** (and Public if you are on Wi-Fi).
- Click **Allow access**.
- If your friend can't connect, check `Windows Defender Firewall with Advanced Security` and make sure UDP traffic is allowed for `MineTunnel.exe`.

### 2. Playing with Non-Premium / "Cracked" Launchers
If anyone in your group is using TLauncher, Prism offline, or similar launchers:
- Dedicated server: Open `server.properties` and change `online-mode=true` to `online-mode=false`, then restart the server.
- Singleplayer LAN: Vanilla Minecraft enforces online mode on LAN worlds. To allow offline accounts on LAN, install a simple mod like *Offline LAN* or *Custom LAN Ports* on the host.

### 3. Modpacks (Forge, Fabric, NeoForge)
- MineTunnel forwards all Minecraft network packets transparently, so mods work with zero configuration.
- Just ensure everyone has the exact same modpack and mod versions installed.

### 4. Connection Troubleshooting
- **Hole punch times out**: If you or your friend is using a mobile phone hotspot (Globe, Smart, DITO mobile data), mobile carriers often use strict Symmetric NAT. Direct P2P might fail on cellular networks. If both players are on wired fiber (PLDT Home Fiber, Converge, Globe Fiber), hole punching works reliably.
- **Relay fallback**: If direct P2P cannot connect due to mobile data NAT, you can switch to the **Relay & Advanced** tab and use a relay server.
