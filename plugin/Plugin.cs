using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Logic;
using Path = System.IO.Path;

namespace URAH_Loader
{
    /// <summary>
    /// Loads the URAH plugins that come with a mod (Workshop or the local Mods folder) from its
    /// plugins/ folder, so players install BepInEx and this loader once and get plugin updates with the
    /// mod. Only DLLs listed in a manifest signed by a trusted key are loaded (see Signature, Manifest).
    ///
    /// The game finds the mods itself (ModManager, Workshop paths included); the loader follows
    /// ModManager.OnActiveModsChanged like ModLink and loads the plugins of each newly active mod. That
    /// event fires from LoadModList before the defs are read, so a plugin's ModLink patches in time.
    /// Mono cannot unload: a mod switched off keeps its plugins loaded, ModLink unpatches them.
    ///
    /// A plugin GUID that is already loaded (a dev build in BepInEx/plugins, loaded by the chainloader
    /// before any mod list exists) wins over the mod's copy. The same plugin in two mods (URAH_MapKit)
    /// loads once, the highest version.
    ///
    /// Plugins loaded from bytes have no Assembly.Location: their plugins/ folder is
    /// AppDomain.CurrentDomain.GetData("urah.loader.dir:" + assembly name).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "urah.koh2.loader";
        public const string Name = "URAH_Loader";
        public const string Version = URAH.BuildVersion.Value; // plugin/version.txt
        public const string DirDataPrefix = "urah.loader.dir:";

        private static ManualLogSource log;
        private static Signature keys;
        private static ModManager subscribed;
        private static readonly HashSet<string> seenMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Manifest.Dll> loaded = new Dictionary<string, Manifest.Dll>();
        private static readonly Dictionary<string, Assembly> assemblies = new Dictionary<string, Assembly>();
        private static readonly Dictionary<Manifest, string> modNames = new Dictionary<Manifest, string>();

        private void Awake()
        {
            log = Logger;
            keys = new Signature(Path.Combine(Path.Combine(Paths.ConfigPath, "urah.loader"), "trusted_keys"), log);
            if (keys.Count == 0)
            {
                log.LogWarning($"{Name} {Version}: no trusted keys, mod plugins are not loaded");
                return;
            }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            new Harmony(Guid).Patch(AccessTools.Constructor(typeof(ModManager)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(ModManagerCtorPostfix)));
            Subscribe(AccessTools.Field(typeof(ModManager), "_instance").GetValue(null) as ModManager);
            log.LogInfo($"{Name} {Version} loaded, {keys.Count} trusted key(s)");
        }

        private static void ModManagerCtorPostfix(ModManager __instance) => Subscribe(__instance);

        private static void Subscribe(ModManager manager)
        {
            if (manager == null || manager == subscribed)
            {
                return;
            }
            subscribed = manager;
            manager.OnActiveModsChanged += OnActiveModsChanged;
            OnActiveModsChanged();
        }

        private static void OnActiveModsChanged()
        {
            try
            {
                LoadNewMods();
            }
            catch (Exception e)
            {
                log.LogError($"Loading mod plugins failed: {e}");
            }
        }

        private static void LoadNewMods()
        {
            var manifests = new List<Manifest>();
            foreach (Mod mod in subscribed.GetActiveMods() ?? new List<Mod>())
            {
                string dir = Path.Combine(mod.base_path, "plugins");
                if (!seenMods.Add(mod.base_path) || !File.Exists(Path.Combine(dir, Manifest.FileName)))
                {
                    continue;
                }
                Manifest m = Manifest.Read(dir, keys, out string error);
                string bad = m?.Files.FirstOrDefault(f => m.ReadVerified(f.Key, f.Value) == null).Key;
                if (m == null || bad != null)
                {
                    log.LogWarning($"Mod {mod.DisplayName}: plugins not loaded, {error ?? bad + " does not match its hash"}");
                    continue;
                }
                modNames[m] = mod.DisplayName;
                manifests.Add(m);
            }
            if (manifests.Count == 0)
            {
                return;
            }

            var plugins = new List<Type>();
            foreach (Manifest.Dll dll in manifests.SelectMany(m => m.Dlls).GroupBy(d => d.Guid)
                         .Select(g => g.OrderByDescending(d => ParseVersion(d.Version)).First())
                         .OrderBy(d => d.Guid, StringComparer.Ordinal))
            {
                plugins.AddRange(Load(dll));
            }
            foreach (Type type in DependencyOrder(plugins))
            {
                StartPlugin(type);
            }
        }

        /// <summary>The plugin types of one DLL, loaded from the bytes its hash was checked on.</summary>
        private static IEnumerable<Type> Load(Manifest.Dll dll)
        {
            string from = $"{dll.File} {dll.Version} from mod {modNames[dll.Owner]}";
            if (loaded.TryGetValue(dll.Guid, out Manifest.Dll prev))
            {
                if (ParseVersion(dll.Version) > ParseVersion(prev.Version))
                {
                    log.LogWarning($"{from} is newer than the loaded {prev.Version}: restart the game to use it");
                }
                return Enumerable.Empty<Type>();
            }
            if (Chainloader.PluginInfos.TryGetValue(dll.Guid, out PluginInfo dev))
            {
                log.LogMessage($"{dll.Guid}: {dev.Location} {dev.Metadata.Version} wins over {from}");
                return Enumerable.Empty<Type>();
            }
            byte[] bytes = dll.Owner.ReadVerified(dll.File, dll.Sha256);
            if (bytes == null)
            {
                log.LogError($"{from}: does not match its hash, not loaded");
                return Enumerable.Empty<Type>();
            }
            Assembly asm = Assembly.Load(bytes);
            string name = asm.GetName().Name;
            assemblies[name] = asm;
            loaded[dll.Guid] = dll;
            AppDomain.CurrentDomain.SetData(DirDataPrefix + name, dll.Owner.Dir);
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
                log.LogWarning($"{from}: some types did not load: {e.LoaderExceptions.FirstOrDefault()?.Message}");
            }
            List<Type> plugins = types.Where(t => typeof(BaseUnityPlugin).IsAssignableFrom(t) && !t.IsAbstract && Meta(t) != null)
                .ToList();
            if (!plugins.Any(t => Meta(t).GUID == dll.Guid))
            {
                log.LogError($"{from}: has no plugin {dll.Guid}, not started");
                return Enumerable.Empty<Type>();
            }
            log.LogInfo($"Loaded {from}");
            return plugins;
        }

        private static void StartPlugin(Type type)
        {
            BepInPlugin meta = Meta(type);
            foreach (BepInDependency dep in type.GetCustomAttributes(typeof(BepInDependency), false))
            {
                bool present = Chainloader.PluginInfos.TryGetValue(dep.DependencyGUID, out PluginInfo info);
                if ((dep.Flags & BepInDependency.DependencyFlags.HardDependency) != 0 &&
                    (!present || info.Metadata.Version < dep.MinimumVersion))
                {
                    log.LogError($"{meta.GUID} not started: needs {dep.DependencyGUID} {dep.MinimumVersion}+, " +
                                 $"{(present ? "found " + info.Metadata.Version : "not loaded")}");
                    return;
                }
            }
            try
            {
                var plugin = (BaseUnityPlugin)Chainloader.ManagerObject.AddComponent(type);
                Chainloader.PluginInfos[meta.GUID] = plugin.Info;
            }
            catch (Exception e)
            {
                log.LogError($"{meta.GUID} not started: {e}");
            }
        }

        /// <summary>Dependencies first (BepInDependency among the given plugins), otherwise by GUID.</summary>
        private static List<Type> DependencyOrder(List<Type> plugins)
        {
            var byGuid = new Dictionary<string, Type>();
            foreach (Type t in plugins)
            {
                byGuid[Meta(t).GUID] = t;
            }
            var order = new List<Type>();
            var seen = new HashSet<string>();
            void Visit(string guid)
            {
                if (!byGuid.TryGetValue(guid, out Type t) || !seen.Add(guid))
                {
                    return;
                }
                foreach (BepInDependency dep in t.GetCustomAttributes(typeof(BepInDependency), false))
                {
                    Visit(dep.DependencyGUID);
                }
                order.Add(t);
            }
            foreach (string guid in byGuid.Keys.OrderBy(g => g, StringComparer.Ordinal))
            {
                Visit(guid);
            }
            return order;
        }

        /// <summary>
        /// A reference to another plugin (URAH_HRE -> URAH_MapKit): Mono does not match it to an assembly
        /// loaded from bytes, so the loaded one is returned; else one already loaded under that name
        /// (a dev copy from BepInEx/plugins).
        /// </summary>
        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (assemblies.TryGetValue(name, out Assembly asm))
            {
                return asm;
            }
            return name.StartsWith("URAH_", StringComparison.Ordinal)
                ? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name)
                : null;
        }

        private static BepInPlugin Meta(Type t) =>
            (BepInPlugin)t.GetCustomAttributes(typeof(BepInPlugin), false).FirstOrDefault();

        private static System.Version ParseVersion(string v) =>
            System.Version.TryParse(v, out System.Version parsed) ? parsed : new System.Version(0, 0);
    }
}
