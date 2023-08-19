// Reference: 0Harmony
using Harmony;
using Oxide.Core.Plugins;
using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Oxide.Plugins
{
    [Info("OxidePerfCounter", "bmgjet", "1.0.0")]
    class OxidePerfCounter : RustPlugin
    {
        private HarmonyInstance _harmony;
        private void Init()
        {
            main.code = new main();
            _harmony = HarmonyInstance.Create(Name + "PATCH");
            Type[] patchType ={AccessTools.Inner(typeof(OxidePerfCounter), "CSharpPlugin_InvokeMethod"),};
            foreach (var t in patchType){new PatchProcessor(_harmony, t, HarmonyMethod.Merge(t.GetHarmonyMethods())).Patch();}
        }
        private void Unload(){_harmony.UnpatchAll(Name + "PATCH"); main.code = null; }


        [HarmonyPatch(typeof(CSharpPlugin), "InvokeMethod", typeof(HookMethod), typeof(object[]))]
        internal class CSharpPlugin_InvokeMethod
        {
            static bool Prefix(HookMethod method, object[] args, CSharpPlugin __instance, ref object __result)
            {
                try
                {
                    main.code.HookPerformance(method, args, __instance, ref __result);
                    return false;
                }
                catch { }
                return true;
            }
        }

        [ConsoleCommand("SaveOxidePerfCounter")]
        private void SaveOxidePerfCounter(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) { return; }
            File.WriteAllText(main.code.GetBackupPath(DateTime.Now) + ".json", JsonConvert.SerializeObject(main.code.PluginInfo));
            Puts("Saved log for " + main.code.PluginInfo.Count + " plugins.");
        }

        [ConsoleCommand("CSVOxidePerfCounter")]
        private void CSVOxidePerfCounter(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) { return; }
            string csv = "Plugin Name,Method Name,Min,AVG,Max" + System.Environment.NewLine;
            foreach (KeyValuePair<string, main.DataTable> d in main.code.PluginInfo)
            {
                foreach (KeyValuePair<string, double[]> dt in d.Value.MethodTime)
                {
                    csv += (d.Key.Replace(',', '.') + "," + dt.Key.Replace(',', '.') + "," + dt.Value[0].ToString().Replace(',', '.') + "," + dt.Value[1].ToString().Replace(',', '.') + "," + dt.Value[2].ToString().Replace(',', '.') + System.Environment.NewLine);
                }
            }
            File.WriteAllText(main.code.GetBackupPath(DateTime.Now) + ".csv", csv);
            Puts("Saved csv for " + main.code.PluginInfo.Count + " plugins.");
        }

        [ConsoleCommand("ClearOxidePerfCounter")]
        private void ClearOxidePerfCounter(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) { return; }
            main.code.PluginInfo.Clear();
            ConsoleSystem.LastError = null;
            Puts("Cleared log for " + main.code.PluginInfo.Count + " plugins.");
        }

        public class main
        {
            public static main code;
            public class DataTable { public Dictionary<string, double[]> MethodTime = new Dictionary<string, double[]>(); }
            public Stopwatch HookTimer = new Stopwatch();
            public bool TimerRunning = false;
            public Dictionary<string, DataTable> PluginInfo = new Dictionary<string, DataTable>();
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
                    if (PluginInfo.ContainsKey(__instance.Name))
                    {
                        DataTable DT = PluginInfo[__instance.Name];
                        if (DT.MethodTime.ContainsKey(method.Name))
                        {
                            if (DT.MethodTime[method.Name][0] > HookTimer.Elapsed.TotalMilliseconds) { DT.MethodTime[method.Name][0] = HookTimer.Elapsed.TotalMilliseconds; }
                            else if (DT.MethodTime[method.Name][2] < HookTimer.Elapsed.TotalMilliseconds) { DT.MethodTime[method.Name][2] = HookTimer.Elapsed.TotalMilliseconds; }
                            DT.MethodTime[method.Name][1] = (double)((DT.MethodTime[method.Name][1] + HookTimer.Elapsed.TotalMilliseconds) / 2);
                        }
                        else
                        {
                            DT.MethodTime.Add(method.Name, new double[3] { HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds });
                        }
                    }
                    else
                    {
                        DataTable DT = new DataTable();
                        DT.MethodTime.Add(method.Name, new double[3] { HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds, HookTimer.Elapsed.TotalMilliseconds });
                        PluginInfo.Add(__instance.Name, DT);
                    }
                }
            }
        }
    }
}