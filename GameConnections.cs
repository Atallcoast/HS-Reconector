using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HearthstoneReconnector
{
    internal sealed class GameConnection
    {
        internal string RemoteAddress;
        internal string LocalAddress;
        internal int LocalPort, RemotePort;
        internal bool SameSocket(GameConnection other) { return RemoteAddress == other.RemoteAddress && LocalAddress == other.LocalAddress && LocalPort == other.LocalPort && RemotePort == other.RemotePort; }
        public override string ToString() { return "local:" + LocalPort + " -> " + RemoteAddress + ":" + RemotePort; }
    }
    internal static class GameConnections
    {
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
        static int Port(int raw) { return ((raw & 255) << 8) | ((raw >> 8) & 255); }
        internal static List<GameConnection> Read(int pid)
        {
            var result = new List<GameConnection>();
            foreach(int family in new int[] { 2, 23 }) {
                int size = 0; uint code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
                if(code != 122 && code != 0) throw new System.ComponentModel.Win32Exception((int)code);
                for(int attempt = 0; attempt < 4; attempt++) {
                    IntPtr memory = Marshal.AllocHGlobal(size);
                    try {
                        code = GetExtendedTcpTable(memory, ref size, false, family, 5, 0);
                        if(code == 122) continue;
                        if(code != 0) throw new System.ComponentModel.Win32Exception((int)code);
                        int stride = family == 2 ? 24 : 56;
                        int count = Marshal.ReadInt32(memory);
                        if(count < 0 || count > (size - 4) / stride) throw new IOException("Invalid TCP table.");
                        for(int i = 0; i < count; i++) {
                            IntPtr row = IntPtr.Add(memory, 4 + i * stride);
                            int owner = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                            int state = Marshal.ReadInt32(row, family == 2 ? 0 : 48);
                            if(owner != pid || state != 5) continue;
                            byte[] bytes = new byte[family == 2 ? 4 : 16];
                            Marshal.Copy(IntPtr.Add(row, family == 2 ? 12 : 24), bytes, 0, bytes.Length);
                            IPAddress address = family == 2 ? new IPAddress(bytes) : new IPAddress(bytes, (uint)Marshal.ReadInt32(row, 40));
                            byte[] localBytes = new byte[family == 2 ? 4 : 16]; Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), localBytes, 0, localBytes.Length);
                            IPAddress local = family == 2 ? new IPAddress(localBytes) : new IPAddress(localBytes, (uint)Marshal.ReadInt32(row,16));
                            if(IPAddress.IsLoopback(address)) continue;
                            result.Add(new GameConnection { LocalAddress = local.ToString(), RemoteAddress = address.ToString(), LocalPort = Port(Marshal.ReadInt32(row, family == 2 ? 8 : 20)), RemotePort = Port(Marshal.ReadInt32(row, family == 2 ? 16 : 44)) });
                        }
                        break;
                    } finally { Marshal.FreeHGlobal(memory); }
                }
            }
            return result;
        }
        internal static GameConnection Select(List<GameConnection> rows, string loggedEndpoint)
        {
            var candidates = new List<GameConnection>();
            foreach(var row in rows) if(row.RemotePort == 3724) candidates.Add(row);
            if(candidates.Count == 1) return candidates[0];
            if(candidates.Count > 1) {
                GameConnection found = null;
                foreach(var row in candidates) if((row.RemoteAddress + ":" + row.RemotePort) == loggedEndpoint) { if(found != null) return null; found = row; }
                return found;
            }
            candidates.Clear();
            if(!string.IsNullOrEmpty(loggedEndpoint))
                foreach(var row in rows) if((row.RemoteAddress + ":" + row.RemotePort) == loggedEndpoint) candidates.Add(row);
            if(candidates.Count == 1) return candidates[0];
            if(candidates.Count > 1) return null;
            return null;
        }
        internal static string ParseEndpoint(string text)
        {
            var matches = Regex.Matches(text, @"Network\.GotoGameServ(?:e|er)\(\).*?address=\s*(\[[0-9a-fA-F:]+\]|[0-9.]+):(\d+)");
            if(matches.Count == 0) return null;
            var last = matches[matches.Count - 1];
            IPAddress address; int port;
            if(!IPAddress.TryParse(last.Groups[1].Value.Trim('[', ']'), out address) || !int.TryParse(last.Groups[2].Value, out port) || port < 1 || port > 65535) return null;
            return address.ToString() + ":" + port;
        }
        internal static string ReadLoggedEndpoint(string gamePath)
        {
            try {
                string root = Path.Combine(Path.GetDirectoryName(gamePath), "Logs");
                if(!Directory.Exists(root)) return null;
                var directories = new List<string>(Directory.GetDirectories(root)); directories.Add(root);
                directories.Sort(delegate(string a, string b) { return Directory.GetLastWriteTimeUtc(b).CompareTo(Directory.GetLastWriteTimeUtc(a)); });
                foreach(string directory in directories.GetRange(0, Math.Min(4, directories.Count)))
                    foreach(string filename in new string[] { "GameNetLogger.log", "Hearthstone.log" }) {
                        string file = Path.Combine(directory, filename);
                        if(!File.Exists(file) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-6)) continue;
                        using(var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                            stream.Seek(Math.Max(0, stream.Length - 262144), SeekOrigin.Begin);
                            using(var reader = new StreamReader(stream, Encoding.UTF8)) { string endpoint = ParseEndpoint(reader.ReadToEnd()); if(endpoint != null) return endpoint; }
                        }
                    }
            } catch(Exception ex) { ConnectionManager.Log("LOG LOOKUP " + ex.Message); }
            return null;
        }
        internal static void SelfTest()
        {
            var session = new GameConnection { RemoteAddress = "37.244.1.1", LocalPort = 50000, RemotePort = 3724 };
            var bnet = new GameConnection { RemoteAddress = "37.244.1.2", LocalPort = 50001, RemotePort = 1119 };
            var rows = new List<GameConnection> { session, bnet };
            if(Select(rows, null) != session || Select(new List<GameConnection> { bnet }, null) != null) throw new Exception("Service connection isolation failed.");
            var second = new GameConnection { RemoteAddress = "37.244.1.3", LocalPort = 50002, RemotePort = 3724 };
            rows.Add(second);
            if(Select(rows, null) != null || Select(rows, "37.244.1.3:3724") != second) throw new Exception("Ambiguity filter failed.");
            if(session.SameSocket(new GameConnection { RemoteAddress = session.RemoteAddress, LocalPort = 50003, RemotePort = 3724 })) throw new Exception("Replacement socket is not isolated.");
            if(ParseEndpoint("Network.GotoGameServe() - address= 37.244.1.3:3724, game=123") != "37.244.1.3:3724") throw new Exception("Log parsing failed.");
            if(ParseEndpoint("Network.GotoGameServe() - address= [2001:db8::1]:3724, game=123") != "2001:db8::1:3724") throw new Exception("IPv6 log parsing failed.");
            if(Port(0x8c0e) != 3724) throw new Exception("TCP port byte order failed.");
            Read(System.Diagnostics.Process.GetCurrentProcess().Id);
        }
    }
}
