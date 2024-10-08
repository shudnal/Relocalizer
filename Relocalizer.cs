﻿using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine.Rendering;
using YamlDotNet.Serialization;
using UnityEngine;

namespace Relocalizer
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public class Relocalizer : BaseUnityPlugin
    {
        public const string pluginID = "shudnal.Relocalizer";
        public const string pluginName = "Relocalizer";
        public const string pluginVersion = "1.0.0";

        private readonly Harmony harmony = new Harmony(pluginID);

        internal static readonly ConfigSync configSync = new ConfigSync(pluginID) { DisplayName = pluginName, CurrentVersion = pluginVersion, MinimumRequiredVersion = pluginVersion };

        internal static Relocalizer instance;

        internal static ConfigEntry<bool> modEnabled;
        internal static ConfigEntry<bool> configLocked;
        internal static ConfigEntry<bool> loggingEnabled;
        internal static ConfigEntry<bool> overwriteDuplicates;

        public static readonly CustomSyncedValue<Dictionary<string, Dictionary<string, string>>> relocalizedStrings = new CustomSyncedValue<Dictionary<string, Dictionary<string, string>>>(configSync, "Relocalized strings", new Dictionary<string, Dictionary<string, string>>());
        public static readonly CustomSyncedValue<Dictionary<string, Dictionary<string, string>>> fixedItemsStrings = new CustomSyncedValue<Dictionary<string, Dictionary<string, string>>>(configSync, "Fixed Items strings", new Dictionary<string, Dictionary<string, string>>());
        public static readonly CustomSyncedValue<Dictionary<string, Dictionary<string, string>>> fixedStatusEffectsStrings = new CustomSyncedValue<Dictionary<string, Dictionary<string, string>>>(configSync, "Fixed Status effects strings", new Dictionary<string, Dictionary<string, string>>());
        public static readonly CustomSyncedValue<Dictionary<string, Dictionary<string, string>>> fixedHoverStrings = new CustomSyncedValue<Dictionary<string, Dictionary<string, string>>>(configSync, "Fixed Hoverable strings", new Dictionary<string, Dictionary<string, string>>());
        public static readonly CustomSyncedValue<Dictionary<string, Dictionary<string, string>>> fixedGlobalStrings = new CustomSyncedValue<Dictionary<string, Dictionary<string, string>>>(configSync, "Fixed Global strings", new Dictionary<string, Dictionary<string, string>>());

        private static string configDirectory;
        private static FileSystemWatcher configWatcher;
        private static float timeToReadConfigs = -1f;

        private static readonly string prefixCurrentLocalization = pluginID + ".CurrentLocalization";
        private static readonly string prefixUnlocalized = pluginID + ".Unlocalized";
        private static readonly string prefixFixedItemsStrings = pluginID + ".FixedStrings.Items";
        private static readonly string prefixFixedStatusEffectsStrings = pluginID + ".FixedStrings.StatusEffects";
        private static readonly string prefixFixedHoverStrings = pluginID + ".FixedStrings.Hover";
        private static readonly string prefixFixedGlobalStrings = pluginID + ".FixedStrings.Global";

        private void Awake()
        {
            harmony.PatchAll();

            instance = this;

            ConfigInit();
            _ = configSync.AddLockingConfigEntry(configLocked);

            relocalizedStrings.ValueChanged += new Action(Relocalize);
            fixedGlobalStrings.ValueChanged += new Action(FixedStringsLocalization.OnLanguageChange);

            Game.isModded = true;

            configDirectory = Path.Combine(Paths.ConfigPath, pluginID);

            SetupConfigWatcher();

            InitCommands();

            FixedStringsLocalization.AddLanguageChangeAction();
        }

        public void ConfigInit()
        {
            config("General", "NexusID", 2868, "Nexus mod ID for updates", false);

            modEnabled = config("General", "Enabled", defaultValue: true, "Enable this mod. Reload the game to take effect.");
            configLocked = config("General", "Lock Configuration", defaultValue: true, "Configuration is locked and can be changed by server admins only.");
            loggingEnabled = config("General", "Logging enabled", defaultValue: false, "Enable logging. [Not Synced with Server]", false);
            overwriteDuplicates = config("General", "Overwrite duplicate keys", defaultValue: true, "Keys loaded from the next file will overwrite values from previous files. [Not Synced with Server]" +
                "\nFiles from config directory are loaded in alphabetical order." +
                "\nIf enabled - duplicate keys will be overwritten if met in the next files" +
                "\nIf disabled - duplicate keys will be ignored", false);
        }

        public void FixedUpdate()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return;

            UpdateReadConfigs();
        }

        private void OnDestroy()
        {
            FixedStringsLocalization.RemoveLanguageChangeAction();
            Config.Save();
            instance = null;
            harmony?.UnpatchSelf();
        }

        public static void LogInfo(object data)
        {
            if (loggingEnabled.Value)
                instance.Logger.LogInfo(data);
        }

        public static void LogWarning(object data)
        {
            instance.Logger.LogWarning(data);
        }

        public static void InitCommands()
        {
            new Terminal.ConsoleCommand("savecurrentlocalization", "[format] - Save current language dictionary", delegate (Terminal.ConsoleEventArgs args)
            {
                string fileName = SaveLocalization(args.ArgsAll.Trim());
                args.Context?.AddString($"Saved {fileName} file to config directory");
            }, optionsFetcher: () => new List<string>() { "yaml", "json" });

            new Terminal.ConsoleCommand("saveunlocalizedstrings", "[format] - Save language dictionary with strings not localized on current language", delegate (Terminal.ConsoleEventArgs args)
            {
                if (Localization.instance.GetSelectedLanguage() == "English")
                {
                    args.Context?.AddString($"Current language English is default language.");
                }
                else
                {
                    string fileName = SaveUnlocalized(args.ArgsAll.Trim());
                    args.Context?.AddString($"Saved {fileName} file to config directory");
                }
            }, optionsFetcher: () => new List<string>() { "yaml", "json" });
        }

        ConfigEntry<T> config<T>(string group, string name, T defaultValue, ConfigDescription description, bool synchronizedSetting = true)
        {
            ConfigEntry<T> configEntry = Config.Bind(group, name, defaultValue, description);

            SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        ConfigEntry<T> config<T>(string group, string name, T defaultValue, string description, bool synchronizedSetting = true) => config(group, name, defaultValue, new ConfigDescription(description), synchronizedSetting);

        #region FileWatcher
        private static void SetupConfigWatcher()
        {
            Directory.CreateDirectory(configDirectory);

            if (configWatcher == null)
            {
                configWatcher = new FileSystemWatcher(configDirectory);
                configWatcher.Changed += new FileSystemEventHandler(StartReadConfigs);
                configWatcher.Created += new FileSystemEventHandler(StartReadConfigs);
                configWatcher.Renamed += new RenamedEventHandler(StartReadConfigs);
                configWatcher.Deleted += new FileSystemEventHandler(StartReadConfigs);
                configWatcher.IncludeSubdirectories = false;
                configWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
                configWatcher.EnableRaisingEvents = true;
            }

            ReadConfigs();
        }

        private static void ReadConfigs()
        {
            Dictionary<string, Dictionary<string, string>> newRelocalized = new Dictionary<string, Dictionary<string, string>>();
            Dictionary<string, Dictionary<string, string>> newFixedItems = new Dictionary<string, Dictionary<string, string>>();
            Dictionary<string, Dictionary<string, string>> newFixedStatusEffects = new Dictionary<string, Dictionary<string, string>>();
            Dictionary<string, Dictionary<string, string>> newFixedGlobal = new Dictionary<string, Dictionary<string, string>>();
            Dictionary<string, Dictionary<string, string>> newFixedHover = new Dictionary<string, Dictionary<string, string>>();

            foreach (FileInfo file in new DirectoryInfo(configDirectory).EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(file => file.Name))
            {
                if (file.Name.StartsWith(prefixCurrentLocalization) || file.Name.StartsWith(prefixUnlocalized))
                    continue;

                string[] filename = file.Name.Split('.');
                if (filename.Length < 2)
                {
                    LogInfo($"Incorrect file name {file.Name}. File name should contain extension.");
                    continue;
                }

                string language = filename[filename.Length - 2];
                if (!Localization.instance.GetLanguages().Contains(language))
                {
                    LogInfo($"Incorrect file name {file.Name}. Language identifier {language} doesn't match any from ingame language list.");
                    continue;
                }

                Dictionary<string, string> dict = ReadConfigFile(file.FullName);
                if (dict == null)
                {
                    LogInfo($"Incorrect file content {file.Name}. Language dictionary can not be loaded.");
                    continue;
                }

                Dictionary<string, Dictionary<string, string>> newValue = GetDict(file.Name);
                if (newValue == null)
                    continue;

                if (!newValue.ContainsKey(language))
                    newValue[language] = dict;
                else if (overwriteDuplicates.Value)
                    dict.Do(kvp => newValue[language][kvp.Key] = kvp.Value);
                else
                    newValue[language] = newValue[language].Concat(dict.Where(x => !newValue[language].Keys.Contains(x.Key))).ToDictionary(x => x.Key, x => x.Value);

                LogInfo($"Loaded file {file.FullName.Replace(configDirectory, "").Substring(1)}.");
            }

            relocalizedStrings.AssignValueSafe(newRelocalized);
            fixedItemsStrings.AssignValueSafe(newFixedItems);
            fixedStatusEffectsStrings.AssignValueSafe(newFixedStatusEffects);
            fixedHoverStrings.AssignValueSafe(newFixedHover);
            fixedGlobalStrings.AssignValueSafe(newFixedGlobal);

            foreach (KeyValuePair<string, Dictionary<string, string>> language in newRelocalized)
                LogInfo($"Language {language.Key} - loaded {language.Value.Count} relocalized keys.");

            foreach (KeyValuePair<string, Dictionary<string, string>> language in fixedItemsStrings.Value)
                LogInfo($"Language {language.Key} - loaded {language.Value.Count} fixed items strings.");
            
            foreach (KeyValuePair<string, Dictionary<string, string>> language in fixedStatusEffectsStrings.Value)
                LogInfo($"Language {language.Key} - loaded {language.Value.Count} fixed status effects strings.");
            
            foreach (KeyValuePair<string, Dictionary<string, string>> language in fixedHoverStrings.Value)
                LogInfo($"Language {language.Key} - loaded {language.Value.Count} fixed hover strings.");
            
            foreach (KeyValuePair<string, Dictionary<string, string>> language in fixedGlobalStrings.Value)
                LogInfo($"Language {language.Key} - loaded {language.Value.Count} fixed global strings.");

            Dictionary<string, Dictionary<string, string>> GetDict(string filename)
            {
                if (filename.StartsWith(prefixFixedItemsStrings))
                    return newFixedItems;
                else if (filename.StartsWith(prefixFixedStatusEffectsStrings))
                    return newFixedStatusEffects;
                else if (filename.StartsWith(prefixFixedHoverStrings))
                    return newFixedHover;
                else if (filename.StartsWith(prefixFixedGlobalStrings))
                    return newFixedGlobal;

                return null;
            }
        }

        private static Dictionary<string, string> ReadConfigFile(string filename)
        {
            try
            {
                string content = File.ReadAllText(filename);
#nullable enable
                if (content != null)
                    return new DeserializerBuilder().IgnoreFields().Build().Deserialize<Dictionary<string, string>?>(content) ?? new Dictionary<string, string>();
#nullable disable
            }
            catch (Exception e)
            {
                LogWarning($"Error reading file ({filename})! Error: {e.Message}");
            }

            return null;
        }

        private static void StartReadConfigs(object sender, FileSystemEventArgs eargs)
        {
            timeToReadConfigs = 0f;
        }

        private static void UpdateReadConfigs()
        {
            if (timeToReadConfigs == -1f)
                return;
            else if (timeToReadConfigs > 1f)
            {
                timeToReadConfigs = -1f;
                ReadConfigs();
            }
            else
                timeToReadConfigs += Time.fixedDeltaTime;
        }
#endregion FileWatcher

        public static string SaveLocalization(string fileFormat)
        {
            bool isJson = fileFormat.ToLower() == "json";
            string filename = $"{prefixCurrentLocalization}.{Localization.instance.GetSelectedLanguage()}.{(isJson ? "json" : "yml")}";

            var serializer = isJson ? new SerializerBuilder().WithIndentedSequences().JsonCompatible().Build() : new Serializer();
            string content = serializer.Serialize(Localization.instance.m_translations).Trim();
            try
            {
                File.WriteAllText(Path.Combine(configDirectory, filename), content);
            }
            catch (Exception e)
            {
                LogWarning($"Error saving file ({filename})! Error: {e.Message}");
                return "";
            }

            return filename;
        }

        public static string SaveUnlocalized(string fileFormat)
        {
            bool isJson = fileFormat.ToLower() == "json";
            string language = Localization.instance.GetSelectedLanguage();
            string filename = $"{prefixUnlocalized}.{language}.{(isJson ? "json" : "yml")}";

            Localization.instance.SetLanguage("English");
            Dictionary<string, string> english = Localization.instance.m_translations.ToDictionary(x => x.Key, x => x.Value);
            Localization.instance.SetLanguage(language);

            var serializer = isJson ? new SerializerBuilder().WithIndentedSequences().JsonCompatible().Build() : new Serializer();
            string content = serializer.Serialize(english.Where(kvp => !Localization.instance.m_translations.ContainsKey(kvp.Key) || Localization.instance.m_translations[kvp.Key] == kvp.Value).ToDictionary(x => x.Key, x => x.Value)).Trim();
            try
            {
                File.WriteAllText(Path.Combine(configDirectory, filename), content);
            }
            catch (Exception e)
            {
                LogWarning($"Error saving file ({filename})! Error: {e.Message}");
                return "";
            }

            return filename;
        }

        public static void Relocalize()
        {
            if (!modEnabled.Value)
                return;

            string language = Localization.instance.GetSelectedLanguage();
            
            Localization.instance.SetLanguage(Localization.instance.GetNextLanguage(language));
            
            for (int i = 0; i < Localization.instance.m_cache.m_capacity; i++)
                Localization.instance.m_cache.Put(i.ToString(), i.ToString());

            Localization.instance.SetLanguage(language);
        }

        public static void AddTranslations(Localization localization, string language)
        {
            if (!relocalizedStrings.Value.ContainsKey(language))
                return;

            relocalizedStrings.Value[language].Do(kvp => localization.AddWord(kvp.Key, kvp.Value));
        }

        [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
        public static class Localization_SetupLanguage_AddLocalizedWords
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Localization __instance, string language)
            {
                AddTranslations(__instance, language);
            }
        }
    }
}
