using HarmonyLib;
using ServerSync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Relocalizer.Relocalizer;

namespace Relocalizer
{
    internal class FixedStringsLocalization
    {
        public class FixedStringsCollection
        {
            private bool haveStringsToLocalize;
            private readonly Dictionary<string, Tuple<bool, string, string>> collection = new Dictionary<string, Tuple<bool, string, string>>();
            private readonly CustomSyncedValue<Dictionary<string, Dictionary<string, string>>> customSyncedValue;

            public FixedStringsCollection(CustomSyncedValue<Dictionary<string, Dictionary<string, string>>> syncedValue)
            {
                customSyncedValue = syncedValue;
            }

            public void OnLanguageChange()
            {
                collection.Clear();
                haveStringsToLocalize = customSyncedValue.Value.ContainsKey(language);
            }

            public bool HaveStringsToLocalize()
            {
                return haveStringsToLocalize;
            }

            public void Localize(string objectName, ref string __result)
            {
                if (!HaveStringsToLocalize())
                    return;

                if (string.IsNullOrWhiteSpace(__result))
                    return;

                if (collection.TryGetValue(objectName, out Tuple<bool, string, string> toLocalize))
                {
                    if (!toLocalize.Item1)
                        return;

                    __result = __result.Replace(toLocalize.Item2, toLocalize.Item3);
                }
                else
                {
                    collection[objectName] = Tuple.Create(false, "", "");
                    foreach (KeyValuePair<string, string> fixedString in customSyncedValue.Value[language])
                        if (__result != (__result = __result.Replace(fixedString.Key, fixedString.Value)))
                            collection[objectName] = Tuple.Create(true, fixedString.Key, fixedString.Value);
                }
            }
        }

        private static readonly FixedStringsCollection hoverableToLocalize = new FixedStringsCollection(fixedHoverStrings);
        private static readonly FixedStringsCollection statusEffectsToLocalize = new FixedStringsCollection(fixedStatusEffectsStrings);
        private static readonly FixedStringsCollection itemsToLocalize = new FixedStringsCollection(fixedItemsStrings);

        private static string language;
        private static bool haveGlobalStringsToLocalize;
        
        public readonly LRUCache<string> cacheGlobal = new LRUCache<string>(100);
        public Dictionary<Text, string> textStrings = new Dictionary<Text, string>();
        public Dictionary<TMP_Text, string> textMeshStrings = new Dictionary<TMP_Text, string>();

        internal static void AddLanguageChangeAction()
        {
            Localization.OnLanguageChange = (Action)Delegate.Combine(Localization.OnLanguageChange, new Action(OnLanguageChange));
        }

        internal static void RemoveLanguageChangeAction()
        {
            Localization.OnLanguageChange = (Action)Delegate.Remove(Localization.OnLanguageChange, new Action(OnLanguageChange));
        }

        internal static void OnLanguageChange()
        {
            language = Localization.instance.GetSelectedLanguage();
            haveGlobalStringsToLocalize = fixedGlobalStrings.Value.ContainsKey(language);

            hoverableToLocalize.OnLanguageChange();
            statusEffectsToLocalize.OnLanguageChange();
            itemsToLocalize.OnLanguageChange();
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new Type[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float) } )]
        public static class ItemData_GetTooltip_LocalizeFixedStrings
        {
            private static void Postfix(ref string __result, ItemDrop.ItemData item)
            {
                itemsToLocalize.Localize("tooltip_" + item.m_shared.m_name, ref __result);
            }
        }

        [HarmonyPatch(typeof(StoreGui), nameof(StoreGui.FillList))]
        public static class StoreGui_FillList_LocalizeFixedStrings
        {
            private static void Postfix(StoreGui __instance)
            {
                Localize(__instance.m_rootPanel.transform);
            }
        }

        [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.GetTooltipString))]
        public static class StatusEffect_GetTooltipString_LocalizeFixedStrings
        {
            private static void Postfix(StatusEffect __instance, ref string __result)
            {
                statusEffectsToLocalize.Localize("setooltip_" + __instance.m_name, ref __result);
            }
        }

        [HarmonyPatch(typeof(Hud), nameof(Hud.UpdateStatusEffects))]
        public static class Hud_UpdateStatusEffects_LocalizeFixedStrings
        {
            private static void Postfix(Hud __instance, List<StatusEffect> statusEffects)
            {
                for (int j = 0; j < statusEffects.Count; j++)
                {
                    StatusEffect statusEffect = statusEffects[j];
                    RectTransform rectTransform = __instance.m_statusEffects[j];
                   
                    TMP_Text name = rectTransform?.Find("Name")?.GetComponent<TMP_Text>();
                    TMP_Text time = rectTransform?.Find("TimeText")?.GetComponent<TMP_Text>();

                    if (name != null && name.isActiveAndEnabled && !string.IsNullOrWhiteSpace(name.text))
                    {
                        string sename = name.text;
                        statusEffectsToLocalize.Localize("sename_" + statusEffect.m_name, ref sename);
                        name.SetText(sename);
                    }

                    if (time != null && time.isActiveAndEnabled && !string.IsNullOrWhiteSpace(time.text))
                    {
                        string setime = time.text;
                        statusEffectsToLocalize.Localize("setime_" + statusEffect.m_name, ref setime);
                        time.SetText(setime);
                    }
                }
            }
        }

        public static void Localize(Transform root)
        {
            if (!haveGlobalStringsToLocalize)
                return;

            foreach (Text text in root.gameObject.GetComponentsInChildren<Text>(includeInactive: true))
                foreach (KeyValuePair<string, string> fixedString in fixedGlobalStrings.Value[language])
                    text.text = text.text.Replace(fixedString.Key, fixedString.Value);

            foreach (TMP_Text text in root.gameObject.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                foreach (KeyValuePair<string, string> fixedString in fixedGlobalStrings.Value[language])
                    text.SetText(text.text.Replace(fixedString.Key, fixedString.Value));
        }

        [HarmonyPatch(typeof(Localization), nameof(Localization.Localize), new Type[] { typeof(string) })]
        public static class Localization_Localize_LocalizeFixedGlobalStrings
        {
            private static void Postfix(ref string __result)
            {
                if (haveGlobalStringsToLocalize && !string.IsNullOrWhiteSpace(__result))
                    foreach (KeyValuePair<string, string> fixedString in fixedGlobalStrings.Value[language])
                        __result = __result.Replace(fixedString.Key, fixedString.Value);
            }
        }

        [HarmonyPatch]
        public static class GetHoverNameAndText
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                return AppDomain.CurrentDomain.GetAssemblies().Where(x => x.FullName.StartsWith("assembly_valheim"))
                                                                .SelectMany(s => s.GetTypes())
                                                                .Where(p => typeof(Hoverable).IsAssignableFrom(p))
                                                                .Where(p => p.Name != "Humanoid" && p.Name != "Player" && p.Name != "Hoverable")
                                                                .SelectMany(t => new List<MethodBase>() { AccessTools.Method(t, "GetHoverName"), AccessTools.Method(t, "GetHoverText") });
            }

            private static void Postfix(MethodBase __originalMethod, object __instance, ref string __result)
            {
                if (!(__instance is Component))
                    return;

                hoverableToLocalize.Localize($"{__originalMethod.Name}_{(__instance as Component).transform.root.gameObject.name}", ref __result);
            }
        }
    }
}
