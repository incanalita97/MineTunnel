using System;
using System.Collections.Generic;
using System.IO;

namespace MineTunnel.Core
{
    public class ConfigManager
    {
        private static ConfigManager _instance;
        private static readonly object _lock = new object();

        public static ConfigManager Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new ConfigManager();
                        _instance.Load();
                    }
                    return _instance;
                }
            }
        }

        public bool IsP2PMode { get; set; }
        public string P2PLastPeerCode { get; set; }
        public string CustomRelayAddress { get; set; }
        public string RoomName { get; set; }
        public string RoomPassword { get; set; }
        public string JoinAddress { get; set; }
        public string JoinRoom { get; set; }
        public string JoinPassword { get; set; }
        public string PlayerName { get; set; }
        public bool IsCustomRelayMode { get; set; }

        private string ConfigPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MineTunnel");
                if (!Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                return Path.Combine(dir, "settings.ini");
            }
        }

        public ConfigManager()
        {
            IsP2PMode = true;
            P2PLastPeerCode = "";
            CustomRelayAddress = "127.0.0.1:25560";
            RoomName = "SteveWorld";
            // Secure default: generate strong random room credentials instead of weak hardcoded strings
            RoomPassword = CryptoSecurity.GenerateRandomToken(8);
            JoinAddress = "127.0.0.1:25560";
            JoinRoom = "SteveWorld";
            JoinPassword = "";
            PlayerName = Environment.UserName;
            IsCustomRelayMode = false;
        }

        public void Load()
        {
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) return;

                string[] lines = File.ReadAllLines(path);
                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;

                    string key = line.Substring(0, idx).Trim();
                    string val = line.Substring(idx + 1).Trim();

                    if (string.Equals(key, "IsP2PMode", StringComparison.OrdinalIgnoreCase))
                    {
                        bool b;
                        if (bool.TryParse(val, out b)) IsP2PMode = b;
                    }
                    else if (string.Equals(key, "P2PLastPeerCode", StringComparison.OrdinalIgnoreCase)) P2PLastPeerCode = val;
                    else if (string.Equals(key, "CustomRelayAddress", StringComparison.OrdinalIgnoreCase)) CustomRelayAddress = val;
                    else if (string.Equals(key, "RoomName", StringComparison.OrdinalIgnoreCase)) RoomName = val;
                    else if (string.Equals(key, "RoomPassword", StringComparison.OrdinalIgnoreCase))
                    {
                        // Unprotect DPAPI protected secret (with fallback for legacy plaintext)
                        RoomPassword = CryptoSecurity.UnprotectSecret(val);
                    }
                    else if (string.Equals(key, "JoinAddress", StringComparison.OrdinalIgnoreCase)) JoinAddress = val;
                    else if (string.Equals(key, "JoinRoom", StringComparison.OrdinalIgnoreCase)) JoinRoom = val;
                    else if (string.Equals(key, "JoinPassword", StringComparison.OrdinalIgnoreCase))
                    {
                        JoinPassword = CryptoSecurity.UnprotectSecret(val);
                    }
                    else if (string.Equals(key, "PlayerName", StringComparison.OrdinalIgnoreCase)) PlayerName = val;
                    else if (string.Equals(key, "IsCustomRelayMode", StringComparison.OrdinalIgnoreCase))
                    {
                        bool b;
                        if (bool.TryParse(val, out b)) IsCustomRelayMode = b;
                    }
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                string path = ConfigPath;
                List<string> lines = new List<string>();
                lines.Add("# MineTunnel Configuration");
                lines.Add("IsP2PMode=" + IsP2PMode);
                lines.Add("P2PLastPeerCode=" + (P2PLastPeerCode ?? ""));
                lines.Add("CustomRelayAddress=" + (CustomRelayAddress ?? ""));
                lines.Add("RoomName=" + (RoomName ?? ""));
                // Protect passwords with Windows DPAPI before writing to disk
                lines.Add("RoomPassword=" + CryptoSecurity.ProtectSecret(RoomPassword ?? ""));
                lines.Add("JoinAddress=" + (JoinAddress ?? ""));
                lines.Add("JoinRoom=" + (JoinRoom ?? ""));
                lines.Add("JoinPassword=" + CryptoSecurity.ProtectSecret(JoinPassword ?? ""));
                lines.Add("PlayerName=" + (PlayerName ?? ""));
                lines.Add("IsCustomRelayMode=" + IsCustomRelayMode);

                File.WriteAllLines(path, lines.ToArray());
            }
            catch { }
        }
    }
}
