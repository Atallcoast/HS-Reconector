using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Text;

namespace HearthstoneReconnector
{
    internal static class ConnectionManager
    {
        internal static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HearthstoneReconnector", "reconnector.log");
        static readonly object Sync = new object();
        internal static void Log(string message)
        {
            lock(Sync) try {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                if(File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024) File.WriteAllText(LogPath, "");
                File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " [" + Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine);
            } catch {}
        }
    }

    internal sealed class RuleSpec
    {
        internal const string Prefix = "HSRC2-";
        internal const string Group = "HearthstoneReconnectorStandaloneV2";
        internal readonly string Name, Path;
        internal readonly int Direction;
        internal readonly GameConnection Target;
        internal RuleSpec(string token, string path, int direction)
            : this(token, path, direction, null) {}
        internal RuleSpec(string token, string path, int direction, GameConnection target)
        {
            Guid parsed;
            if(!Guid.TryParseExact(token, "N", out parsed) || (direction != 1 && direction != 2)) throw new ArgumentException("Invalid rule identity.");
            Name = Prefix + token + (direction == 1 ? "-in" : "-out"); Path = path; Direction = direction; Target = target;
        }
        internal static bool IsOwned(string name, string group)
        {
            if(group != Group || name == null || !name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            string suffix = name.Substring(Prefix.Length);
            Guid guid;
            return suffix.Length >= 35 && Guid.TryParseExact(suffix.Substring(0, 32), "N", out guid) && (suffix.Substring(32) == "-in" || suffix.Substring(32) == "-out");
        }
    }

    internal interface IFirewallBackend
    {
        void Add(RuleSpec rule);
        void Remove(string name);
    }

    internal sealed class FirewallBackend : IFirewallBackend
    {
        readonly dynamic policy;
        internal FirewallBackend() { policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)); }
        internal void CheckEnabled()
        {
            int profiles = policy.CurrentProfileTypes;
            foreach(int profile in new int[] { 1, 2, 4 })
                if((profiles & profile) != 0 && !(bool)policy.FirewallEnabled[profile]) throw new InvalidOperationException("Брандмауэр Windows выключен для активной сети. Включите его в настройках Windows.");
            if((int)policy.LocalPolicyModifyState != 0) throw new InvalidOperationException("Политика Windows не разрешает применение локальных правил.");
        }
        public void Add(RuleSpec spec)
        {
            dynamic rule = CreateRule(spec);
            try {
                policy.Rules.Add(rule);
                ConnectionManager.Log("ADD " + spec.Name + " app=" + spec.Path + " direction=" + spec.Direction + " target=" + spec.Target);
            } finally { Marshal.FinalReleaseComObject(rule); }
        }
        internal static object CreateRule(RuleSpec spec)
        {
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true));
            try {
                rule.Name = spec.Name; rule.Description = "Temporary Hearthstone traffic block; automatically removed.";
                if(spec.Target == null) throw new InvalidOperationException("Отсутствует точное соединение матча.");
                rule.ApplicationName = spec.Path; rule.Protocol = 6;
                rule.LocalPorts = spec.Target.LocalPort.ToString(); rule.RemotePorts = spec.Target.RemotePort.ToString();
                rule.RemoteAddresses = spec.Target.RemoteAddress; rule.Direction = spec.Direction;
                rule.Profiles = int.MaxValue; rule.Action = 0; rule.Grouping = RuleSpec.Group; rule.Enabled = true;
                return rule;
            } catch { Marshal.FinalReleaseComObject(rule); throw; }
        }
        public void Remove(string name)
        {
            // Restrict removal to this app's own uniquely named rules.
            if(!RuleSpec.IsOwned(name, RuleSpec.Group)) throw new ArgumentException("Foreign rule.");
            policy.Rules.Remove(name);
            foreach(dynamic rule in (IEnumerable)policy.Rules)
                if((string)rule.Name == name && (string)rule.Grouping == RuleSpec.Group) throw new IOException("Правило ещё присутствует: " + name);
            ConnectionManager.Log("REMOVE " + name);
        }
        internal static void CleanStale()
        {
            var backend = new FirewallBackend();
            var names = new List<string>();
            foreach(dynamic rule in (IEnumerable)backend.policy.Rules)
                if(RuleSpec.IsOwned((string)rule.Name, (string)rule.Grouping)) names.Add((string)rule.Name);
            BlockSession.Restore(backend, names.ToArray(), Thread.Sleep);
        }
        internal static void ReadOnlyProbe()
        {
            var backend = new FirewallBackend();
            Console.WriteLine("Firewall COM available; current profiles=" + (int)backend.policy.CurrentProfileTypes);
            Console.WriteLine("Existing rule count=" + (int)backend.policy.Rules.Count);
        }
    }

    internal static class BlockSession
    {
        internal static void Run(IFirewallBackend backend, RuleSpec[] rules, Action wait)
        {
            var names = new List<string>(); foreach(var rule in rules) names.Add(rule.Name);
            try { foreach(var rule in rules) backend.Add(rule); wait(); }
            finally { Restore(backend, names.ToArray(), Thread.Sleep); }
        }
        internal static void Restore(IFirewallBackend backend, string[] names, Action<int> pause)
        {
            Exception last = null;
            var pending = new List<string>(names);
            for(int attempt = 0; attempt < 5 && pending.Count > 0; attempt++)
            {
                foreach(string name in pending.ToArray())
                    try { backend.Remove(name); pending.Remove(name); }
                    catch(Exception ex) { last = ex; ConnectionManager.Log("Restore attempt " + attempt + ": " + ex.Message); }
                if(pending.Count > 0) pause(300);
            }
            if(pending.Count > 0) throw new IOException("Не удалось снять временную блокировку. Запустите RestoreConnection.cmd.", last);
        }
    }

    internal static class FirewallWorker
    {
        internal static ProcessStartInfo StartInfo(string arguments)
        { return new ProcessStartInfo(Application.ExecutablePath, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }; }
        internal static string Launch(int pid, string cancelEvent)
        {
            var info = StartInfo("--direct " + pid + " " + cancelEvent);
            info.RedirectStandardOutput = true;
            info.StandardOutputEncoding = new UTF8Encoding(false);
            using(var worker = Process.Start(info))
            {
                string output = worker.StandardOutput.ReadToEnd();
                worker.WaitForExit();
                if(worker.ExitCode != 0) throw new IOException(output.Length > 0 ? output : "Помощник блокировки завершился с ошибкой.");
                return output;
            }
        }
        internal static int Dispatch(string[] args)
        {
            try {
                switch(args[0]) {
                    case "--direct": if(args.Length!=3)throw new ArgumentException(); using(var cancel=EventWaitHandle.OpenExisting(args[2])) { WriteOutput(DirectReconnect.Execute(int.Parse(args[1]),cancel));return 0; }
                    case "--utf8-test": WriteOutput("Переподключение подтверждено.");return 0;
                    
                    
                    case "--restore": FirewallBackend.CleanStale(); MessageBox.Show("Временные правила Reconnector удалены."); return 0;
                    case "--self-test": DirectReconnect.SelfTest(); WriteOutput("PASS: exact tuple, socket replacement, IPv6 refusal, TCP tables, selection. Native close requires elevated integration testing.");return 0;
                    case "--probe": FirewallBackend.ReadOnlyProbe(); return 0;
                    default: throw new ArgumentException("Неизвестный параметр.");
                }
            } catch(Exception ex) { ConnectionManager.Log(ex.ToString()); WriteOutput(ex.Message); if(args[0] == "--restore") MessageBox.Show(ex.Message); return 1; }
        }
        static void WriteOutput(string text)
        { try { using(var writer = new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) { writer.WriteLine(text); writer.Flush(); } } catch {} }
    }
}

