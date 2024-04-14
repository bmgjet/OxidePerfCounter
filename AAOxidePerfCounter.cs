// Reference: 0Harmony
/*
 ▄▄▄▄    ███▄ ▄███▓  ▄████  ▄▄▄██▀▀▀▓█████▄▄▄█████▓
▓█████▄ ▓██▒▀█▀ ██▒ ██▒ ▀█▒   ▒██   ▓█   ▀▓  ██▒ ▓▒
▒██▒ ▄██▓██    ▓██░▒██░▄▄▄░   ░██   ▒███  ▒ ▓██░ ▒░
▒██░█▀  ▒██    ▒██ ░▓█  ██▓▓██▄██▓  ▒▓█  ▄░ ▓██▓ ░ 
░▓█  ▀█▓▒██▒   ░██▒░▒▓███▀▒ ▓███▒   ░▒████▒ ▒██▒ ░ 
░▒▓███▀▒░ ▒░   ░  ░ ░▒   ▒  ▒▓▒▒░   ░░ ▒░ ░ ▒ ░░   
▒░▒   ░ ░  ░      ░  ░   ░  ▒ ░▒░    ░ ░  ░   ░    
 ░    ░ ░      ░   ░ ░   ░  ░ ░ ░      ░    ░      
 ░*/
using Harmony;
using Oxide.Core.Plugins;
using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("AAOxidePerfCounter", "bmgjet", "1.0.0")]
    class AAOxidePerfCounter : RustPlugin
    {
        //Setting
        public int SaveDataEachMin = 360; //Set to value over 0 to disable (60 = 1 hour)

        //Vars
        public static AAOxidePerfCounter plugin;
        public static OxidePerf Perf;
        private HarmonyInstance _harmony;
        private Coroutine _coroutine;
        float PluginStartTime;
        public class OxidePerf
        {
            public Dictionary<string, ulong> HookCalls = new Dictionary<string, ulong>();
            public Dictionary<string, DataTable> PluginInfo = new Dictionary<string, DataTable>();
        }
        public class DataTable { public Dictionary<string, double[]> MethodTime = new Dictionary<string, double[]>(); }
        public Stopwatch HookTimer = new Stopwatch();
        public bool TimerRunning = false;

        #region Commands

        [ConsoleCommand("SaveOxidePerfCounter")]
        private void SaveOxidePerfCounter(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) { return; }
            File.WriteAllText(plugin.GetBackupPath(DateTime.Now) + ".json", JsonConvert.SerializeObject(Perf));
            Puts("Saved log for " + Perf.PluginInfo.Count + " plugins.");
        }

        [ConsoleCommand("CSVOxidePerfCounter")]
        private void CSVOxidePerfCounter(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) { return; }
            SaveCSV();
        }

        [ConsoleCommand("OPCReport")]
        private void ReportOxidePerfCounter(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) { return; }
            SaveCSV(true);
        }

        [ConsoleCommand("ClearOxidePerfCounter")]
        private void ClearOxidePerfCounter(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) { return; }
            Perf.PluginInfo.Clear();
            ConsoleSystem.LastError = null;
            Puts("Cleared log for " + Perf.PluginInfo.Count + " plugins.");
        }

        #endregion

        #region Oxide Hooks
        private void Init()
        {
            PluginStartTime = Time.realtimeSinceStartup;
            Perf = new OxidePerf();
            plugin = this;
            _harmony = HarmonyInstance.Create(Name + "PATCH");
            Type[] patchType = { AccessTools.Inner(typeof(AAOxidePerfCounter), "CSharpPlugin_InvokeMethod"), AccessTools.Inner(typeof(AAOxidePerfCounter), "Plugin_CallHook"), };
            foreach (var t in patchType) { new PatchProcessor(_harmony, t, HarmonyMethod.Merge(t.GetHarmonyMethods())).Patch(); }
        }
        void OnServerInitialized() { if (SaveDataEachMin > 0) { timer.Every(SaveDataEachMin * 60, () => { SaveCSV(true); }); } }
        private void Unload()
        {
            plugin = null;
            Perf = null;
            _harmony.UnpatchAll(Name + "PATCH");
        }

        #endregion

        #region Harmony Hooks
        [HarmonyPatch(typeof(CSharpPlugin), "InvokeMethod", typeof(HookMethod), typeof(object[]))]
        internal class CSharpPlugin_InvokeMethod
        {
            [HarmonyPrefix]
            static bool Prefix(HookMethod method, object[] args, CSharpPlugin __instance, ref object __result)
            {
                try
                {
                    plugin.HookPerformance(method, args, __instance, ref __result);
                    return false;
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(Plugin), "CallHook", typeof(string), typeof(object[]))]
        internal class Plugin_CallHook
        {
            public static Dictionary<string, int> Hooks = new Dictionary<string, int>();
            [HarmonyPostfix]
            static void Postfix(string hook)
            {
                try
                {
                    if (Perf.HookCalls.ContainsKey(hook)) { Perf.HookCalls[hook]++; }
                    else { Perf.HookCalls.Add(hook, 1); }
                }
                catch { }
            }
        }
        #endregion

        #region Methods
        public void SaveCSV(bool report = false)
        {
            if (_coroutine != null) { ServerMgr.Instance.StopCoroutine(_coroutine); }
            string csv = "";
            if (report)
            {
                foreach (var hk in from entry in Perf.HookCalls orderby entry.Value descending select entry)
                {
                    csv += hk.Key + "," + hk.Value + ",,,,,," + System.Environment.NewLine;
                }
                TimeSpan t = TimeSpan.FromSeconds(Time.realtimeSinceStartup - PluginStartTime);
                string Runtime = string.Format("{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                t.Hours,
                t.Minutes,
                t.Seconds,
                t.Milliseconds);
                csv += System.Environment.NewLine + System.Environment.NewLine + "Logging Time:," + Runtime + ",,,,,," + System.Environment.NewLine;
            }
            Puts("Saving csv for " + Perf.PluginInfo.Count + " plugins.");
            _coroutine = ServerMgr.Instance.StartCoroutine(MakeFile(report, csv));
        }

        public string GetBackupPath(DateTime date)
        {
            return string.Format("{0}/{1}_{2}_{3}_{4}_{5}", new object[]
            {
                    ConVar.Server.GetServerFolder("OxidePerfCounter"),
                    date.Minute,
                    date.Hour,
                    date.Day,
                    date.Month,
                    date.Year
            });
        }

        public void HookPerformance(HookMethod method, object[] args, CSharpPlugin __instance, ref object __result)
        {
            bool flag = !TimerRunning && (method.Method.Name.Length >= 0);
            if (flag)
            {
                TimerRunning = true;
                HookTimer.Restart();
            }
            try
            {
                if (!method.IsBaseHook && args != null && args.Length != 0)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        object obj = args[i];
                        if (obj != null)
                        {
                            Type parameterType = method.Parameters[i].ParameterType;
                            bool isValueType = parameterType.IsValueType;
                            if (isValueType)
                            {
                                if (parameterType != typeof(object) && obj.GetType() != parameterType)
                                {
                                    args[i] = Convert.ChangeType(obj, parameterType);
                                }
                            }
                        }
                    }
                }
                if (!__instance.DirectCallHook(method.Name, out __result, args)) { __result = method.Method.Invoke(__instance, args); }
            }
            catch (Exception ex)
            {
                string[] array = new string[5];
                array[0] = "Failed to call hook ";
                int num = 1;
                string text;
                if (method == null) { text = null; }
                else
                {
                    string name = method.Name;
                    text = ((name != null) ? name.ToString() : null);
                }
                array[num] = (text ?? "NULL");
                array[2] = ":\n";
                array[3] = ((ex != null) ? ex.ToString() : null);
                array[4] = "\n\n\n";
                __instance.RaiseError(string.Concat(array));
            }
            if (flag)
            {
                HookTimer.Stop();
                TimerRunning = false;
                if (__instance.Name == "AAOxidePerfCounter") { return; }
                if (Perf.PluginInfo.ContainsKey(__instance.Name))
                {
                    DataTable DT = Perf.PluginInfo[__instance.Name];
                    if (DT.MethodTime.ContainsKey(method.Name))
                    {
                        if (DT.MethodTime[method.Name][0] > HookTimer.Elapsed.TotalMilliseconds) { DT.MethodTime[method.Name][0] = HookTimer.Elapsed.TotalMilliseconds; }
                        else if (DT.MethodTime[method.Name][2] < HookTimer.Elapsed.TotalMilliseconds) { DT.MethodTime[method.Name][2] = HookTimer.Elapsed.TotalMilliseconds; }
                        DT.MethodTime[method.Name][1] = (double)((DT.MethodTime[method.Name][1] + HookTimer.Elapsed.TotalMilliseconds) / 2);
                        DT.MethodTime[method.Name][3] = (double)((DT.MethodTime[method.Name][3] + HookTimer.Elapsed.TotalMilliseconds));
                        DT.MethodTime[method.Name][4] = ((double)(__instance.TotalHookMemory));
                        DT.MethodTime[method.Name][5]++;
                    }
                    else
                    {
                        DT.MethodTime.Add(method.Name, new double[6] { HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds, __instance.TotalHookMemory, 1 });
                    }
                }
                else
                {
                    DataTable DT = new DataTable();
                    DT.MethodTime.Add(method.Name, new double[6] { HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds, __instance.TotalHookMemory, 1 });
                    Perf.PluginInfo.Add(__instance.Name, DT);
                }
            }
        }

        private IEnumerator MakeFile(bool report, string hookfired)
        {
            Puts("Generating Hook CSV");
            string csv = "Plugin Name,Hook Name,Min[ms],Avg[ms],Max[ms],Total Run Time[ms],Memory[KB],Hook Triggered" + System.Environment.NewLine;
            Dictionary<string, double> worstorder = new Dictionary<string, double>();
            foreach (KeyValuePair<string, DataTable> d in Perf.PluginInfo)
            {
                foreach (KeyValuePair<string, double[]> dt in d.Value.MethodTime)
                {
                    try
                    {
                        if (!dt.Key.StartsWith("OnServerInitialized") && dt.Key != "Init" && dt.Key != "Unload" && dt.Key != "Loaded")
                        {
                            worstorder.Add(d.Key.Replace(',', '.') + "," + dt.Key.Replace(',', '.'), dt.Value[3]);
                        }
                        csv += (d.Key.Replace(',', '.') + "," + dt.Key.Replace(',', '.') + "," + dt.Value[0].ToString().Replace(',', '.') + "," + dt.Value[1].ToString().Replace(',', '.') + "," + dt.Value[2].ToString().Replace(',', '.') + "," + dt.Value[3].ToString().Replace(',', '.') + "," + (dt.Value[4] / 1024f).ToString().Replace(',', '.') + "," + (dt.Value[5]).ToString().Replace(',', '.') + System.Environment.NewLine);
                    }
                    catch { }
                }
            }
            if (report)
            {
                Puts("Building Worst Hooks List");
                yield return CoroutineEx.waitForSeconds(0.0035f);
                int limit = 0;
                try
                {
                    csv += System.Environment.NewLine + System.Environment.NewLine + "Worst In Order,,,,,,," + System.Environment.NewLine;
                    foreach (var worst in from entry in worstorder orderby entry.Value descending select entry)
                    {
                        limit++;
                        if (limit > 50) { break; }
                        csv += (worst.Key + "," + worst.Value.ToString().Replace(',', '.') + ",,,,," + System.Environment.NewLine);
                    }
                }
                catch { }
                Puts("Building Hook Counter");
                yield return CoroutineEx.waitForSeconds(0.0035f);
                if (!string.IsNullOrEmpty(hookfired))
                {
                    csv += System.Environment.NewLine + System.Environment.NewLine + "Hook Name,Hook Fired,,,,,," + System.Environment.NewLine + hookfired;
                }
                Puts("Building Entity Count List");
                yield return CoroutineEx.waitForSeconds(0.0035f);
                csv += System.Environment.NewLine + System.Environment.NewLine + "Entity,Amount,,,,,," + System.Environment.NewLine;
                int checks = 0;
                int last = 0;
                int done = 0;
                int loops = BaseNetworkable.serverEntities.entityList.Count();
                Dictionary<string, ulong> values = new Dictionary<string, ulong>();
                foreach (var ent in BaseNetworkable.serverEntities.entityList.Values.ToList()) //List since might be changed during wait times.
                {
                    checks++;
                    done++;
                    if (++checks >= 10000) //Check conditions so many loops
                    {
                        //Limit rate based on FPS
                        if (Performance.report.frameRate < 15 && ConVar.FPS.limit > 15) { yield return CoroutineEx.waitForSeconds(0.01f); }
                        else { yield return CoroutineEx.waitForSeconds(0.0035f); }
                        checks = 0;
                        //Output Percentage Debug
                        if (done > last)
                        {
                            last += (loops / 5);
                            Puts("Scanning Entitys " + (int)Math.Round((double)(100 * done) / loops) + "%");
                        }
                    }
                    if (ent != null)
                    {
                        try
                        {
                            if (!values.ContainsKey(ent?.ShortPrefabName))
                            {
                                values.Add(ent?.ShortPrefabName, 1);
                            }
                            else
                            {
                                values[ent?.ShortPrefabName]++;
                            }
                        }
                        catch { }
                    }
                }
                yield return CoroutineEx.waitForSeconds(0.0035f);
                Puts("Sorting Values");
                foreach (var ent in from entry in values orderby entry.Value descending select entry)
                {
                    try
                    {
                        csv += ent.Key + "," + ent.Value + ",,,,,," + System.Environment.NewLine;
                    }
                    catch { }
                }
                yield return CoroutineEx.waitForSeconds(0.0035f);
                Puts("Saving File");
                File.WriteAllText(GetBackupPath(DateTime.Now) + ".csv", csv);
                yield return CoroutineEx.waitForSeconds(0.0035f);
                Perf.HookCalls.Clear();
                Perf.PluginInfo.Clear();
                PluginStartTime = Time.realtimeSinceStartup;
            }
        }
        #endregion
    }
}