/*▄▄▄    ███▄ ▄███▓  ▄████  ▄▄▄██▀▀▀▓█████▄▄▄█████▓
▓█████▄ ▓██▒▀█▀ ██▒ ██▒ ▀█▒   ▒██   ▓█   ▀▓  ██▒ ▓▒
▒██▒ ▄██▓██    ▓██░▒██░▄▄▄░   ░██   ▒███  ▒ ▓██░ ▒░
▒██░█▀  ▒██    ▒██ ░▓█  ██▓▓██▄██▓  ▒▓█  ▄░ ▓██▓ ░ 
░▓█  ▀█▓▒██▒   ░██▒░▒▓███▀▒ ▓███▒   ░▒████▒ ▒██▒ ░ 
░▒▓███▀▒░ ▒░   ░  ░ ░▒   ▒  ▒▓▒▒░   ░░ ▒░ ░ ▒ ░░   
▒░▒   ░ ░  ░      ░  ░   ░  ▒ ░▒░    ░ ░  ░   ░    
 ░    ░ ░      ░   ░ ░   ░  ░ ░ ░      ░    ░      
 ░             ░         ░  ░   ░      ░  */
using HarmonyLib;
using System;
using Oxide.Core.Plugins;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using System.Text;
namespace Oxide.Plugins
{
    [Info("AAAMethodTimer", "bmgjet", "1.0.1")]
    class AAAMethodTimer : RustPlugin
    {
        public Harmony harmony;
        public Coroutine coroutine = null;
        public static AAAMethodTimer plugin;
        private IDictionary<string, AssemblyData> Patched = new Dictionary<string, AssemblyData>();
        public Stopwatch HookTimer = new Stopwatch();
        public bool TimerRunning = false;
        private bool ServerStarting = false;
        List<string> Blacklist;
        string[] BlacklistMethods = new string[] { "directcallhook", "movenext", "tostring", "memberwiseclone", "gettype", "gethashcode", "finalize", "equals", "isinvoking", "stopallcoroutines", "stopcoroutine", "startcoroutine", "adduniversalcommand", "addcovalencecommand", "raiseerror", "trackstart", "trackend", "<>m__finally1", "<>m__finally2", "system.idisposable.dispose", "system.collections.generic.ienumerator<system.object>.get_current", "system.collections.ienumerator.reset", "system.collections.ienumerator.get_current", "getcomponentsinchildren", "getcomponentsinparent", "printtoconsole", "printtochat", "sendreply", "getcomponentinchildren", "getcomponentinparent", "getcomponents", "sendmessageupwards", "sendmessage", "broadcastmessage", "get_name", "get_typeid"};

        public class AssemblyData
        {
            public IDictionary<MethodInfo, MethodLog> MethodRunTime = new Dictionary<MethodInfo, MethodLog>();
            public IDictionary<MethodBase, MethodInfo> Patches = new Dictionary<MethodBase, MethodInfo>();
        }

        public class DataRecord
        {
            public Stopwatch stopwatch = new Stopwatch();
            public long memory = 0;
            public DataRecord Init() { stopwatch.Start(); return this; }
        }

        public class MethodLog
        {
            public TimeSpan TotalHookTime = new TimeSpan();
            public int HookCount = 0;
            public long MemoryAllocations = 0;
            public MethodLog(TimeSpan totalHookTime, int hookCount, long MemUse) { TotalHookTime = totalHookTime; HookCount = hookCount; MemoryAllocations = MemUse; }
        }
        #region Oxide Hooks

        private void Init()
        {
            plugin = this;
            harmony = new Harmony(Name + "_Patch");
            //Create Black list from system files
            DirectoryInfo d = new DirectoryInfo(Path.Combine("RustDedicated_Data", "Managed"));
            FileInfo[] files = d.GetFiles("*.dll");
            Blacklist = files.Select(x => x.Name.Replace(".dll", "")).ToList();
            for (int i = Blacklist.Count - 1; i > 0; i--) { if (Blacklist[i].StartsWith("Oxide.Ext.")) { Blacklist.Remove(Blacklist[i]); } } //Allow Extensions
            Ready(); //check if ready
        }

        void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin.Name == Name){return;}
            for (int i = Patched.Count - 1; i >= 0; i--)
            {
                bool found = false;
                foreach (var p in Patched.ElementAt(i).Value.Patches)
                {
                    if (p.Key.DeclaringType.Name.Contains(plugin.Name) && Patched.ElementAt(i).Value.Patches.Count > 0)
                    {
                        found = true; break;
                    }
                }
                if (found)
                {
                    Puts("Unpatching Plugin " + plugin.Name);
                    foreach (var p in Patched.ElementAt(i).Value.Patches)
                    {
                        harmony.Unpatch(p.Key, p.Value);
                    }
                    Patched.ElementAt(i).Value.Patches.Clear();
                    Patched.ElementAt(i).Value.MethodRunTime.Clear();
                }
            }
        }

        private void Unload()
        {
            //Save data
            SaveInfo();
            SavePluginInfo();
            //unload
            if (plugin != null) { plugin = null; }
            if (coroutine != null) { ServerMgr.Instance.StopCoroutine(coroutine); }
            coroutine = ServerMgr.Instance.StartCoroutine(Startup(false));
        }
        #endregion

        #region Startup Loop
        private void Ready()
        {
            if (ServerMgr.Instance == null) //Server No start enough for a coroutine
            {
                ServerStarting = true;
                timer.Once(1, () => { Ready(); }); //Check if ready again 1 sec
                return;
            }
            //Patch Oxide Hook Caller
            Puts("Patching All Plugin Methods, Server May Freeze For A While!!!");
            if (ServerStarting) { ScanForNewAssemblys(); } //Server first start so thread lock doesnt matter to players
            coroutine = ServerMgr.Instance.StartCoroutine(Startup(true)); //Start checker
        }

        private IEnumerator Startup(bool Starting)
        {
            if (Starting) //Starting
            {
                yield return CoroutineEx.waitForSeconds(10); //Wait for code to load
                int methodChecks = 0;
                while (true)
                {
                    //Check again for new plugins every 5sec
                    yield return CoroutineEx.waitForSeconds(5);
                    yield return ScanForNewAssemblys(methodChecks);
                }
            }
            else
            {
                //Unload patches
                if (harmony != null)
                {
                    Puts("UnPatching " + Patched.Count + " Assemblies Might Take A While And Freeze/Kick Players");
                    int patchChecks = 0;
                    foreach (var patch in Patched)
                    {
                        foreach (var p in patch.Value.Patches)
                        {
                            if (patchChecks++ > 50)
                            {
                                yield return CoroutineEx.waitForSeconds(0.01f); //Attempt to prevent freeze
                                patchChecks = 0;
                            }
                            harmony.Unpatch(p.Key, p.Value);
                        }
                    }
                }
            }
        }
        #endregion

        #region CUI

        [ConsoleCommand("exportmethodinfo")]
        private void exportmethodinfo(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                if (arg.Player().IsAdmin)
                {
                    SaveInfo(arg.Player());
                    CuiHelper.DestroyUi(arg.Player(), "MTUI");
                }
            }
        }

        [ConsoleCommand("exportplugininfo")]
        private void exportplugininfo(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                if (arg.Player().IsAdmin)
                {
                    SavePluginInfo(arg.Player());
                    CuiHelper.DestroyUi(arg.Player(), "MTUI");
                }
            }
        }

        [ConsoleCommand("clearmethodinfo")]
        private void clearmethodinfo(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                if (arg.Player().IsAdmin)
                {
                    foreach (var p in Patched.Values){p.MethodRunTime.Clear();}
                    arg.Player().ChatMessage("Cleared Logging Info");
                    CuiHelper.DestroyUi(arg.Player(), "MTUI");
                }
            }
        }

        [ChatCommand("plugininfo")]
        private void ShowPluginInfo(BasePlayer player)
        {
            Plugin[] plugins = this.plugins.GetAll().Where((Plugin p) => p.Filename != null).ToArray();
            int loops = plugins.Length;
            int offset = (loops - (int)(loops * 0.25f)) - 25;
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                CursorEnabled = true,
                Image = { Color = ".1 .1 .1 1.0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, "Overlay", "MTUI", "MTUI");

            container.Add(new CuiElement
            {
                Name = "SB",
                Parent = "MTUI",
                Components = {
                    new CuiScrollViewComponent {
                        MovementType = UnityEngine.UI.ScrollRect.MovementType.Elastic,
                        Vertical = true,
                        Inertia = true,
                        Horizontal = false,
                        Elasticity = 0.25f,
                        DecelerationRate = 0.3f,
                        ScrollSensitivity = 24f,
                        ContentTransform = new CuiRectTransform { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 " + ((25 * (loops + 2)) * -1), OffsetMax = "0 0" },
                        VerticalScrollbar = new CuiScrollbar() { Size = 20f, AutoHide = false },
                    },
                    new CuiRawImageComponent
                    {
                        Sprite = "assets/content/effects/crossbreed/fx gradient skewed.png",
                        Color = ".05 .05 .05 .5"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Name = "Title",
                Parent = "MTUI",
                Components = {
                        new CuiRawImageComponent
                        {Sprite = "assets/content/ui/ui.background.transparent.radial.psd", Color = ".25 .25 .25 1",},
                        new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -50", OffsetMax = "-18 0"},
                        }
            });

            container.Add(new CuiElement
            {
                Name = "TT",
                Parent = "MTUI",
                Components = {
                    new CuiTextComponent { Text = PadBoth("Plugin Name", 44) + PadBoth("Plugin File Name", 35) + PadBoth("Mem [kb]", 20) + PadBoth("Total [ms]", 20) + PadBoth("Version", 10), Font = "droidsansmono.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter },
                    new CuiOutlineComponent { Color = "0 0 0 1", Distance = "1 1" },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -50", OffsetMax = "-18 0"},
                }
            });
            foreach (var data in plugins)
            {
                offset -= 25;
                string name = offset.ToString();
                container.Add(new CuiElement
                {
                    Name = name,
                    Parent = "SB",
                    Components = {
                        new CuiRawImageComponent
                        {
                            Sprite = "assets/content/ui/ui.background.transparent.radial.psd",
                        Color = ".2 .2 .2 1",
                        },
                        new CuiRectTransformComponent { AnchorMin = ".03 .97", AnchorMax = ".97 .97", OffsetMin = "0 " + (offset - 24).ToString(), OffsetMax = "0 " + name},
                        }
                });
                container.Add(new CuiElement
                {
                    Name = "_" + name,
                    Parent = name,
                    Components = {
                        new CuiTextComponent { Text = PadBoth(data.Name, 40).PadLeft(0) + PadBoth(data.Filename.Basename(), 50)+ PadBoth((data.TotalHookMemory / 1024L).ToString(), 20) + PadBoth((data.TotalHookTime).ToString("0.0000"), 20) + PadBoth(data.Version.ToString(), 10), Font = "droidsansmono.ttf", FontSize = 12, Align = TextAnchor.MiddleCenter },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1"},
                        }
                });
            }
            container.Add(new CuiButton
            {
                Button = { Color = ".5 .2 .2 1", Close = "MTUI" },
                Text = { Text = "X", Font = "droidsansmono.ttf", FontSize = 20, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = ".94 .93", AnchorMax = ".98 1" }
            }, "MTUI", "CloseBttn");

            container.Add(new CuiButton
            {
                Button = { Color = ".2 .3 .2 1", Command = "exportplugininfo" },
                Text = { Text = "Export", Font = "droidsansmono.ttf", FontSize = 16, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0 .93", AnchorMax = ".06 1" }
            }, "MTUI", "ExportBttn");
            CuiHelper.AddUi(player, container);
        }

        [ChatCommand("methodinfo")]
        private void ShowMethodInfo(BasePlayer player)
        {
            if (player.IsAdmin)
            {
                Dictionary<MethodInfo, MethodLog> Clean = CleanAndSort();
                int loops = Clean.Count;
                int offset = (loops - (int)(loops * 0.25f)) - 25;
                var container = new CuiElementContainer();
                container.Add(new CuiPanel
                {
                    CursorEnabled = true,
                    Image = { Color = ".1 .1 .1 1.0" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, "Overlay", "MTUI", "MTUI");

                container.Add(new CuiElement
                {
                    Name = "SB",
                    Parent = "MTUI",
                    Components = {
                    new CuiScrollViewComponent {
                        MovementType = UnityEngine.UI.ScrollRect.MovementType.Elastic,
                        Vertical = true,
                        Inertia = true,
                        Horizontal = false,
                        Elasticity = 0.25f,
                        DecelerationRate = 0.3f,
                        ScrollSensitivity = 24f,
                        ContentTransform = new CuiRectTransform { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 " + ((25 * (loops + 2)) * -1), OffsetMax = "0 0" },
                        VerticalScrollbar = new CuiScrollbar() { Size = 20f, AutoHide = false },
                    },
                    new CuiRawImageComponent
                    {
                        Sprite = "assets/content/effects/crossbreed/fx gradient skewed.png",
                        Color = ".05 .05 .05 .5"
                    }
                }
                });

                container.Add(new CuiElement
                {
                    Name = "Title",
                    Parent = "MTUI",
                    Components = {
                        new CuiRawImageComponent
                        {Sprite = "assets/content/ui/ui.background.transparent.radial.psd", Color = ".25 .25 .25 1",},
                        new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -50",OffsetMax = "-18 0"},
                        }
                });

                container.Add(new CuiElement
                {
                    Name = "TT",
                    Parent = "MTUI",
                    Components = {
                    new CuiTextComponent { Text = PadBoth("Plugin Name", 35) + PadBoth("Method Name", 35) + PadBoth("AVG [ms]", 15) + PadBoth("Total [ms]", 15) + PadBoth("#Invokes", 10) + PadBoth("Memory", 10), Font = "droidsansmono.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter },
                    new CuiOutlineComponent { Color = "0 0 0 1", Distance = "1 1" },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -50",OffsetMax = "-18 0" },
                }
                });
                foreach (var data in Clean)
                {
                    offset -= 25;
                    string name = offset.ToString();
                    container.Add(new CuiElement
                    {
                        Name = name,
                        Parent = "SB",
                        Components = {
                        new CuiRawImageComponent
                        {
                            Sprite = "assets/content/ui/ui.background.transparent.radial.psd",
                        Color = ".2 .2 .2 1",
                        },
                        new CuiRectTransformComponent { AnchorMin = ".03 .97", AnchorMax = ".97 .97", OffsetMin = "0 " + (offset - 24).ToString(), OffsetMax = "0 " + name},
                        }
                    });

                    Type Parent = data.Key.DeclaringType;
                    string ParentType = Parent?.Name;
                    while (Parent.DeclaringType != null)
                    {
                        ParentType = Parent.DeclaringType?.Name + "/" + ParentType;
                        Parent = Parent.DeclaringType;
                    }
                    container.Add(new CuiElement
                    {
                        Name = "_" + name,
                        Parent = name,
                        Components = {
                        new CuiTextComponent { Text = PadBoth(ParentType, 35).PadLeft(0) + PadBoth(data.Key.Name, 45) + PadBoth((data.Value.TotalHookTime.TotalMilliseconds / (double)data.Value.HookCount).ToString("0.0000"), 20) + PadBoth(data.Value.TotalHookTime.TotalMilliseconds.ToString("0.0000"), 20) + PadBoth(data.Value.HookCount.ToString(), 10) + PadBoth(data.Value.MemoryAllocations.ToString("0"), 10), Font = "droidsansmono.ttf", FontSize = 12, Align = TextAnchor.MiddleCenter },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1"},
                        }
                    });
                }

                container.Add(new CuiButton
                {
                    Button = { Color = ".5 .2 .2 1", Close = "MTUI" },
                    Text = { Text = "X", Font = "droidsansmono.ttf", FontSize = 16, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = ".94 .93", AnchorMax = ".98 1" }
                }, "MTUI", "CloseBttn");

                container.Add(new CuiButton
                {
                    Button = { Color = ".3 .2 .2 1", Command = "clearmethodinfo" },
                    Text = { Text = "Clear", Font = "droidsansmono.ttf", FontSize = 16, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = "0 .93", AnchorMax = ".06 1" }
                }, "MTUI", "ClearBttn");

                container.Add(new CuiButton
                {
                    Button = { Color = ".2 .3 .2 1", Command = "exportmethodinfo" },
                    Text = { Text = "Export", Font = "droidsansmono.ttf", FontSize = 16, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = ".07 .93", AnchorMax = ".13 1" }
                }, "MTUI", "ExportBttn");
                CuiHelper.AddUi(player, container);
            }
        }

        #endregion

        #region Methods
        public static string RemoveSpecialCharacters(string str)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in str)
            {
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '.' || c == '_' || c == ' ' || c == '[' || c == ']' || c == '/' || c == '\\' || c == '@' || c == '(' || c == ')' || c == '{' || c == '}' || c == '#')
                {
                    sb.Append(c);
                }
                else { sb.Append('_'); }
            }
            string output = sb.ToString();
            return (output.StartsWith("_____________________")) ? "Obfuscated_MethodName" : output;
        }

        private string PadBoth(string source, int length)
        {
            source = RemoveSpecialCharacters(source);
            int spaces = length - source.Length;
            int padLeft = spaces / 2 + source.Length;
            return WithMaxLength(source.PadLeft(padLeft).PadRight(length), length);
        }

        private string WithMaxLength(string value, int maxLength) { return value?.Substring(0, Math.Min(value.Length, maxLength)); }

        private Dictionary<MethodInfo, MethodLog> CleanAndSort()
        {
            var Clean = new Dictionary<MethodInfo, MethodLog>();
            foreach (var p in Patched.Values)
            {
                foreach (var methodinfo in p.MethodRunTime) //Read Out Direct Hook Info (More Accurate)
                {
                    if (methodinfo.Key.Name.EndsWith(")")) { Clean.Add(methodinfo.Key, methodinfo.Value); }
                }
                foreach (var methodinfo in p.MethodRunTime) //Add the rest but not duplcates that have a direct hook value already
                {
                    bool c = false;
                    foreach (var k in Clean.Keys)
                    {
                        Type Parent = methodinfo.Key.DeclaringType;
                        string ParentType = Parent?.Name;
                        while (Parent.DeclaringType != null)
                        {
                            ParentType = Parent.DeclaringType?.Name + "/" + ParentType;
                            Parent = Parent.DeclaringType;
                        }
                        if (k.Name.Contains(methodinfo.Key.Name) || ParentType.Contains(Name))
                        {
                            c = true;
                            break;
                        }
                    }
                    if (!c && !Clean.ContainsKey(methodinfo.Key))
                    {
                        if(methodinfo.Value.MemoryAllocations < 0){methodinfo.Value.MemoryAllocations = 0;}
                        Clean.Add(methodinfo.Key, methodinfo.Value);
                    }
                }
            }
            return Clean.Keys.OrderBy(k => k.DeclaringType.Name).ToDictionary(k => k, k => Clean[k]);
        }

        private void SaveInfo(BasePlayer player = null)
        {
            int methods = 0;
            foreach (var p in Patched.Values)
            {
                if (p.MethodRunTime != null && p.MethodRunTime.Count > 0)
                {
                    string datastring = "Plugin Name,Method Name,AVG Time[ms],Total Time[ms],#Invokes,Memory Allocations" + System.Environment.NewLine;
                    foreach (var data in CleanAndSort())
                    {
                        Type Parent = data.Key.DeclaringType;
                        string ParentType = Parent?.Name;
                        while (Parent.DeclaringType != null)
                        {
                            ParentType = Parent.DeclaringType?.Name + "/" + ParentType;
                            Parent = Parent.DeclaringType;
                        }
                        try
                        {
                            datastring += ParentType + "," + data.Key.Name + "," + (data.Value.TotalHookTime.TotalMilliseconds / (double)data.Value.HookCount) + "," + data.Value.TotalHookTime.TotalMilliseconds + "," + data.Value.HookCount.ToString() + "," + data.Value.MemoryAllocations + System.Environment.NewLine;
                        }
                        catch { }
                    }
                    methods += p.MethodRunTime.Count;
                    File.WriteAllText(GetBackupPath("Method", DateTime.Now), datastring);
                }
            }
            Puts("Saved Data For " + methods + " Methods");
            if (player != null) { player.ChatMessage("Saved Data For " + methods + " Methods"); }
        }

        private void SavePluginInfo(BasePlayer player = null)
        {
            Plugin[] plugins = this.plugins.GetAll().Where((Plugin p) => p.Filename != null).ToArray();
            if (plugins != null && plugins.Length > 0)
            {
                string datastring = "Plugin Name,File Name,Mem [kb],Total [ms],Version" + System.Environment.NewLine;
                foreach (var data in plugins)
                {
                    try
                    {
                        datastring += data.Name + "," + data.Filename.Basename() + "," + (data.TotalHookMemory / 1024L).ToString() + "," + (data.TotalHookTime).ToString("0.0000") + "," + data.Version.ToString() + System.Environment.NewLine;
                    }
                    catch { }
                }
                File.WriteAllText(GetBackupPath("Plugin", DateTime.Now), datastring);
                Puts("Saved Data For " + plugins.Count() + " Plugins");
                if (player != null) { player.ChatMessage("Saved Data For " + plugins.Count() + " Plugins"); }
            }
        }

        private string GetBackupPath(string prefix, DateTime date)
        {
            return Path.Combine(ConVar.Server.GetServerFolder("MethodTimer"), string.Format(prefix + "_{0}_{1}_{2}_{3}_{4}.csv", new object[]
              {
                    date.Minute,
                    date.Hour,
                    date.Day,
                    date.Month,
                    date.Year
              }));
        }

        private void ScanForNewAssemblys()
        {
            int patched = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) //Scan Loaded Modules
            {
                if (assembly == null) { continue; }
                string n = assembly?.GetName()?.Name;
                if (string.IsNullOrEmpty(n) || Blacklist.Contains(n) || Patched.ContainsKey(n)) { continue; } //Only Target harmony and oxide plugins/extentions
                AssemblyData assemblyData = new AssemblyData();
                foreach (Type type in assembly.GetTypes()) //get all types
                {
                    try
                    {
                        if (type == null || string.IsNullOrEmpty(type?.Name)) { continue; }
                        if (type.Name != this.Name && !type.Name.StartsWith("<") && !type.Name.Contains("<>")) //Dont patch self or it will be endless loop
                        {
                            foreach (MethodInfo mi2 in type.GetRuntimeMethods()) //get all methods
                            {
                                if (!BlacklistMethods.Contains(mi2?.Name.ToLower())) //Make sure its not blacklisted
                                {
                                    try
                                    {
                                        //Add pre/postfix stopwatch
                                        assemblyData.Patches.Add(mi2, harmony.Patch(mi2, new HarmonyMethod(AccessTools.Method(typeof(AAAMethodTimer), "Prefix")), new HarmonyMethod(AccessTools.Method(typeof(AAAMethodTimer), "Postfix"))));
                                        patched++;
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }
                }
                Patched.Add(n, assemblyData); //Add to list so dont check it again.
            }
            if (patched != 0) { Puts("Patched " + patched + " Methods"); } //Only Output on new patches added
        }

        //Same as above but in coroutines not to thread lock server
        private IEnumerator ScanForNewAssemblys(int Checks)
        {
            int patched = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null) { continue; }
                string n = assembly?.GetName()?.Name;
                if (string.IsNullOrEmpty(n) || Blacklist.Contains(n) || Patched.ContainsKey(n)) { continue; }
                AssemblyData assemblyData = new AssemblyData();
                foreach (Type type in assembly.GetTypes())
                {
                    if (type == null || string.IsNullOrEmpty(type?.Name)) { continue; }
                    if (type?.Name != this.Name && !type.Name.Contains("<>") && !type.Name.Contains("OfType"))
                    {
                        foreach (MethodInfo mi2 in type.GetRuntimeMethods())
                        {
                            if (Checks++ > 40)
                            {
                                if (Performance.report.frameRateAverage < 15) { yield return CoroutineEx.waitForSeconds(0.01f); }
                                else { yield return CoroutineEx.waitForSeconds(0.003f); }
                                Checks = 0;
                            }
                            string name = mi2?.DeclaringType?.Name;
                            if (!BlacklistMethods.Contains(mi2?.Name.ToLower()) && !name.Contains(Name))
                            {
                                try
                                {
                                    assemblyData.Patches.Add(mi2, harmony.Patch(mi2, new HarmonyMethod(AccessTools.Method(typeof(AAAMethodTimer), "Prefix")), new HarmonyMethod(AccessTools.Method(typeof(AAAMethodTimer), "Postfix"))));
                                    patched++;
                                }
                                catch { }
                            }
                        }
                    }
                }
                Patched.Add(n, assemblyData);
            }
            if (patched != 0) { Puts("Patched " + patched + " New Methods"); }
        }

        //Run a normal oxide hook call but do it with a stopwatch to time it.
        public void HookPerformance(HookMethod method, object[] args, CSharpPlugin __instance, ref object __result)
        {
            bool flag = !TimerRunning && (method.Method.Name.Length >= 0);
            if (flag)
            {
                TimerRunning = true;
                HookTimer.Restart();
            }
            long Memory = -1;
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
                if (__instance.Name.Contains(Name)) { return; } //Dont log self
                foreach (var p in Patched.Values)
                {
                    if (p.MethodRunTime.ContainsKey(method.Method)) { p.MethodRunTime[method.Method].MemoryAllocations += Memory; p.MethodRunTime[method.Method].TotalHookTime = p.MethodRunTime[method.Method].TotalHookTime.Add(HookTimer.Elapsed); p.MethodRunTime[method.Method].HookCount++; }
                    else { p.MethodRunTime.Add(method.Method, new MethodLog(HookTimer.Elapsed, 1, Memory)); }
                }
            }
        }

        #endregion

        #region Harmony
        [AutoPatch]
        [HarmonyPatch(typeof(CSharpPlugin), "InvokeMethod", typeof(HookMethod), typeof(object[]))]
        internal class Performance_Update
        {
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

        static void Prefix(out DataRecord __state)
        {
            try
            {
                __state = new DataRecord(); // assign your own state
                __state.memory = GC.GetTotalMemory(false);
                __state.stopwatch = Stopwatch.StartNew();
                return;
            }
            catch { }
            __state = null;
        }

        static void Postfix(DataRecord __state, MethodInfo __originalMethod)
        {
            if (__state != null)
            {
                __state.stopwatch.Stop();
                try
                {
                    if (plugin != null)
                    {
                        foreach (var p in plugin.Patched.Values)
                        {
                            if (p.MethodRunTime.ContainsKey(__originalMethod)) { p.MethodRunTime[__originalMethod].TotalHookTime = p.MethodRunTime[__originalMethod].TotalHookTime.Add(__state.stopwatch.Elapsed); p.MethodRunTime[__originalMethod].HookCount++; p.MethodRunTime[__originalMethod].MemoryAllocations += (GC.GetTotalMemory(false) - __state.memory) / 1024; }
                            else { p.MethodRunTime.Add(__originalMethod, new MethodLog(__state.stopwatch.Elapsed, 1, (GC.GetTotalMemory(false) - __state.memory) / 1024)); }
                        }
                    }
                }
                catch { }
            }
        }
        #endregion
    }
}