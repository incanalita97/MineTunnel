# MineTunnel Automated Test Suite
$ErrorActionPreference = "Stop"

$exePath = "C:\Users\SPIRO\.gemini\antigravity\scratch\remote-admin\bin\MineTunnel.exe"
Write-Host "Loading assembly: $exePath" -ForegroundColor Cyan
$asm = [System.Reflection.Assembly]::LoadFrom($exePath)

$passCount = 0
$failCount = 0

function Assert-Equal($actual, $expected, $testName) {
    if ($actual -eq $expected) {
        Write-Host "  [PASS] $testName" -ForegroundColor Green
        $global:passCount++
    } else {
        Write-Host "  [FAIL] $testName : Expected '$expected', got '$actual'" -ForegroundColor Red
        $global:failCount++
    }
}

Write-Host "`n--- Test 1: Minecraft Protocol VarInt Serialization ---" -ForegroundColor Yellow
$ms = New-Object System.IO.MemoryStream
[MineTunnel.Common.MinecraftProtocol]::WriteVarInt($ms, 25565)
$ms.Position = 0
$val = [MineTunnel.Common.MinecraftProtocol]::ReadVarInt($ms)
Assert-Equal $val 25565 "VarInt 25565 Roundtrip"

$ms.SetLength(0)
[MineTunnel.Common.MinecraftProtocol]::WriteVarInt($ms, 0)
$ms.Position = 0
$valZero = [MineTunnel.Common.MinecraftProtocol]::ReadVarInt($ms)
Assert-Equal $valZero 0 "VarInt 0 Roundtrip"

Write-Host "`n--- Test 2: Minecraft LAN Announcement Parser ---" -ForegroundColor Yellow
$lanPacketBytes = [MineTunnel.Common.MinecraftProtocol]::BuildLanAnnouncement("EpicSurvival - Steve", 54321)
$lanString = [System.Text.Encoding]::UTF8.GetString($lanPacketBytes)
$motd = ""
$port = 0
$parsed = [MineTunnel.Common.MinecraftProtocol]::TryParseLanAnnouncement($lanString, [ref]$motd, [ref]$port)
Assert-Equal $parsed $true "LAN Announcement Parsing Status"
Assert-Equal $motd "EpicSurvival - Steve" "LAN MOTD Match"
Assert-Equal $port 54321 "LAN Port Match"

Write-Host "`n--- Test 3: Tunnel Framing & Packet Magic ---" -ForegroundColor Yellow
$memStream = New-Object System.IO.MemoryStream
$payload = [System.Text.Encoding]::UTF8.GetBytes("Hello Minecraft World")
[MineTunnel.Common.TunnelPacket]::WritePacket($memStream, [MineTunnel.Common.PacketType]::ChannelData, $payload)
$memStream.Position = 0
$packet = [MineTunnel.Common.TunnelPacket]::ReadPacket($memStream)
Assert-Equal $packet.Type ([MineTunnel.Common.PacketType]::ChannelData) "Packet Type Match"
$readString = [System.Text.Encoding]::UTF8.GetString($packet.Payload)
Assert-Equal $readString "Hello Minecraft World" "Packet Payload Data Match"

Write-Host "`n--- Test 4: Auth Request & Response Serialization ---" -ForegroundColor Yellow
$authReq = [MineTunnel.Common.TunnelPacket]::BuildAuthRequest("MyRoom", "SuperSecretPass", "Alex")
$rRoom = ""; $rPass = ""; $rName = ""
[MineTunnel.Common.TunnelPacket]::ParseAuthRequest($authReq, [ref]$rRoom, [ref]$rPass, [ref]$rName)
Assert-Equal $rRoom "MyRoom" "Auth Request Room Name"
Assert-Equal $rPass "SuperSecretPass" "Auth Request Password"
Assert-Equal $rName "Alex" "Auth Request Player Name"

$authResp = [MineTunnel.Common.TunnelPacket]::BuildAuthResponse($true, 42, "")
$rOk = $false; $rId = 0; $rErr = ""
[MineTunnel.Common.TunnelPacket]::ParseAuthResponse($authResp, [ref]$rOk, [ref]$rId, [ref]$rErr)
Assert-Equal $rOk $true "Auth Response Success Flag"
Assert-Equal $rId 42 "Auth Response Client ID"

Write-Host "`n--- Test 5: Live Loopback Tunnel Test (Host & Client) ---" -ForegroundColor Yellow
$testPort = 29550
$tunnelHost = New-Object MineTunnel.Core.TunnelHost
$started = $tunnelHost.Start($testPort, "TestRoom", "Secret123", 25565)
Assert-Equal $started $true "TunnelHost Start on port $testPort"

$tunnelClient = New-Object MineTunnel.Core.TunnelClient
$connected = $tunnelClient.Connect("127.0.0.1", $testPort, "TestRoom", "Secret123", "TestPlayer")
Assert-Equal $connected $true "TunnelClient Connected & Authenticated"

# Clean up
$tunnelClient.Disconnect()
$tunnelHost.Stop()
Write-Host "  [PASS] Clean Disconnect" -ForegroundColor Green
$global:passCount++

Write-Host "`n--- Test 6: STUN Request & P2P Code Parser ---" -ForegroundColor Yellow
$stunReq = [MineTunnel.Core.StunClient]::BuildBindingRequest()
Assert-Equal $stunReq.Length 20 "STUN 20-byte Binding Request Length"
Assert-Equal $stunReq[0] 0 "STUN Message Type High Byte"
Assert-Equal $stunReq[1] 1 "STUN Message Type Low Byte (0x0001)"

$token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
$code = "MINE-P2P#112.198.10.20:49500#SurvivalWorld#" + $token
$pEp = $null; $rName = ""
$pOk = [MineTunnel.Core.P2PHolePuncher]::TryParseInviteCode($code, [ref]$pEp, [ref]$rName)
Assert-Equal $pOk $true "P2P Code Parser Status"
Assert-Equal $pEp.Address.ToString() "112.198.10.20" "P2P Code IP Match"
Assert-Equal $pEp.Port 49500 "P2P Code Port Match"
Assert-Equal $rName "SurvivalWorld" "P2P Code Room Name Match"

Write-Host "`n--- Test 7: ConfigManager P2P Persistence ---" -ForegroundColor Yellow
$origP2PCode = [MineTunnel.Core.ConfigManager]::Instance.P2PLastPeerCode
[MineTunnel.Core.ConfigManager]::Instance.P2PLastPeerCode = "MINE-P2P#120.28.1.1:40000#MyWorld"
[MineTunnel.Core.ConfigManager]::Instance.IsP2PMode = $true
[MineTunnel.Core.ConfigManager]::Instance.Save()
[MineTunnel.Core.ConfigManager]::Instance.P2PLastPeerCode = ""
[MineTunnel.Core.ConfigManager]::Instance.Load()
Assert-Equal ([MineTunnel.Core.ConfigManager]::Instance.P2PLastPeerCode) "MINE-P2P#120.28.1.1:40000#MyWorld" "ConfigManager P2PLastPeerCode Persistence"
Assert-Equal ([MineTunnel.Core.ConfigManager]::Instance.IsP2PMode) $true "ConfigManager IsP2PMode Persistence"
[MineTunnel.Core.ConfigManager]::Instance.P2PLastPeerCode = $origP2PCode
[MineTunnel.Core.ConfigManager]::Instance.Save()

Write-Host "`n========================================================"
Write-Host " Tests Passed: $global:passCount | Tests Failed: $global:failCount" -ForegroundColor $(if ($global:failCount -eq 0) { "Green" } else { "Red" })
Write-Host "========================================================`n"

if ($global:failCount -ne 0) {
    exit 1
}
