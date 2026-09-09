using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RestfulTweaks.Cleaned
{
    public static class ModernShopCompat
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Restful Tweaks Redux Clean");
        private static Type pluginType;
        private static bool initialized;
        private static bool databaseReady;
        private static bool configInitialized;
        private static bool lastDaily;
        private static bool lastAllItems;
        private static bool lastUnlimited;
        private static float nextConfigCheck;

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                pluginType = FindType("RestfulTweaks.Plugin");
                var harmony = new Harmony("net.nep.bepinex.restfultweaks.cleanedcompat");
                var refresh = pluginType != null ? FindMethod(pluginType, "ShopRefresh", 0) : null;
                if (refresh != null)
                {
                    harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(ModernShopCompat).GetMethod(nameof(ShopRefreshPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    Log.LogInfo("Replaced deprecated ShopRefresh backend with native weekly shop regeneration path.");
                }
                var accessor = FindType("ShopDatabaseAccessor");
                var accessorAwake = accessor != null ? FindMethod(accessor, "Awake", 0) : null;
                if (accessorAwake != null)
                {
                    harmony.Patch(accessorAwake, postfix: new HarmonyMethod(typeof(ModernShopCompat).GetMethod(nameof(ShopDatabaseAwakePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                    Log.LogInfo("Hooked ShopDatabaseAccessor.Awake for deferred shop initialization.");
                }
                var update = pluginType != null ? FindMethod(pluginType, "Update", 0) : null;
                if (update != null)
                    harmony.Patch(update, postfix: new HarmonyMethod(typeof(ModernShopCompat).GetMethod(nameof(PluginUpdatePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                DescribeNativeShopRefreshCandidates();
                Log.LogInfo("Cleaned shop compatibility initialized. No UI polling or vendor lifecycle hooks.");
            }
            catch (Exception e) { Log.LogError("Modern compatibility initialization failed: " + Unwrap(e)); }
        }

        private static void ShopDatabaseAwakePostfix()
        {
            databaseReady = true;
            try { ReadAndApplyConfig(false); }
            catch (Exception e) { Log.LogError("Shop settings initialization failed: " + Unwrap(e)); }
        }

        private static void PluginUpdatePostfix()
        {
            if (!databaseReady || Time.unscaledTime < nextConfigCheck) return;
            nextConfigCheck = Time.unscaledTime + 0.75f;
            try { ReadAndApplyConfig(true); }
            catch (Exception e) { Log.LogWarning("Shop config check failed: " + Unwrap(e).Message); }
        }

        private static bool ShopRefreshPrefix()
        {
            if (!databaseReady)
            {
                Log.LogWarning("Shop refresh ignored because the shop database is not ready yet.");
                return false;
            }
            try
            {
                ApplySettingsToAllShops();
                if (!TryInvokeNativeWeeklyRefresh())
                    Log.LogError("Native weekly shop regeneration method could not be invoked. Check the log for candidates.");
                else
                    ApplySettingsToAllShops();
            }
            catch (Exception e) { Log.LogError("Native weekly shop reroll failed: " + Unwrap(e)); }
            return false;
        }

        private static void ReadAndApplyConfig(bool refreshOnChange)
        {
            if (!databaseReady) return;
            if (pluginType == null) pluginType = FindType("RestfulTweaks.Plugin");
            if (pluginType == null) return;
            bool daily = ReadConfigBool("_shopUpdateDaily");
            bool allItems = ReadConfigBool("_shopAllItems");
            bool unlimited = ReadConfigBool("_shopMoreItems");
            bool changed = !configInitialized || daily != lastDaily || allItems != lastAllItems || unlimited != lastUnlimited;
            if (!changed) return;
            bool first = !configInitialized;
            configInitialized = true;
            lastDaily = daily;
            lastAllItems = allItems;
            lastUnlimited = unlimited;
            ApplySettingsToAllShops();
            Log.LogInfo("Shop settings applied: Update Stock Daily=" + daily + ", All Items=" + allItems + ", Unlimited Items=" + unlimited + ".");
            if (refreshOnChange && !first && TryInvokeNativeWeeklyRefresh())
            {
                ApplySettingsToAllShops();
                Log.LogInfo("Shop setting changed; native weekly regeneration invoked.");
            }
        }

        private static bool ReadConfigBool(string fieldName)
        {
            var field = pluginType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (field == null) return false;
            object entry = field.GetValue(null);
            if (entry == null) return false;
            var value = entry.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (value != null && value.PropertyType == typeof(bool)) return (bool)value.GetValue(entry, null);
            var boxed = entry.GetType().GetProperty("BoxedValue", BindingFlags.Public | BindingFlags.Instance);
            if (boxed != null)
            {
                object v = boxed.GetValue(entry, null);
                if (v is bool) return (bool)v;
            }
            return false;
        }

        private static void ApplySettingsToAllShops()
        {
            var shops = GetAllShops();
            foreach (object shop in shops)
            {
                if (shop == null) continue;
                if (lastDaily) SetEveryDay(shop);
                object rawItems = GetMemberValue(shop, "shopItems");
                if (!(rawItems is IEnumerable items)) continue;
                foreach (object item in items)
                {
                    if (item == null) continue;
                    if (lastAllItems) SetBoolMember(item, "alwaysAppear", true);
                    if (lastUnlimited) SetBoolMember(item, "unlimited", true);
                }
            }
        }

        private static void SetEveryDay(object shop)
        {
            object current = GetMemberValue(shop, "updateDays");
            if (!(current is IList) || !current.GetType().IsGenericType) return;
            try
            {
                Type listType = current.GetType();
                Type dayType = listType.GetGenericArguments()[0];
                if (!dayType.IsEnum) return;
                object replacement = Activator.CreateInstance(listType);
                IList list = (IList)replacement;
                string[] names = { "Mon", "Tue", "Wed", "Thurs", "Fri", "Sat", "Sun" };
                foreach (string name in names) { try { list.Add(Enum.Parse(dayType, name, true)); } catch { } }
                SetMemberValue(shop, "updateDays", replacement);
            }
            catch { }
        }

        private static bool TryInvokeNativeWeeklyRefresh()
        {
            Type managerType = FindType("ShopsManager");
            if (managerType == null) { Log.LogWarning("ShopsManager type not found."); return false; }
            object manager = FindInstance(managerType);
            string[] names = { "CreateNewShops", "InitializeShopLists", "CheckShops" };
            foreach (string name in names)
            {
                MethodInfo method = BestMethod(managerType, name);
                if (method == null) continue;
                try
                {
                    method.Invoke(method.IsStatic ? null : manager, BuildArguments(method));
                    Log.LogInfo("Native shop reroll invoked: ShopsManager." + method.Name + Signature(method) + ".");
                    return true;
                }
                catch (Exception e) { Log.LogWarning("ShopsManager." + name + " invocation failed: " + Unwrap(e).Message); }
            }
            return false;
        }

        private static void DescribeNativeShopRefreshCandidates()
        {
            Type t = FindType("ShopsManager");
            if (t == null) return;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                if (m.Name == "CreateNewShops" || m.Name == "InitializeShopLists" || m.Name == "CheckShops")
                    Log.LogInfo("Native shop candidate: ShopsManager." + m.Name + Signature(m));
        }

        private static MethodInfo BestMethod(Type type, string name)
        {
            MethodInfo best = null;
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.Name != name) continue;
                if (best == null || m.GetParameters().Length < best.GetParameters().Length) best = m;
            }
            return best;
        }

        private static string Signature(MethodInfo m)
        {
            var p = m.GetParameters();
            string[] parts = new string[p.Length];
            for (int i = 0; i < p.Length; i++) parts[i] = p[i].ParameterType.Name + " " + p[i].Name;
            return "(" + string.Join(", ", parts) + ")";
        }

        private static object[] BuildArguments(MethodInfo method)
        {
            var p = method.GetParameters();
            var args = new object[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i].HasDefaultValue) args[i] = p[i].DefaultValue;
                else if (p[i].ParameterType == typeof(bool)) args[i] = true;
                else if (p[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(p[i].ParameterType);
                else args[i] = null;
            }
            return args;
        }

        private static List<object> GetAllShops()
        {
            var result = new List<object>();
            if (!databaseReady) return result;
            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null) return result;
            MethodInfo getAll = FindMethod(accessorType, "GetAllShops", 0);
            if (getAll == null) return result;
            object accessor = FindInstance(accessorType);
            object raw;
            try { raw = getAll.Invoke(getAll.IsStatic ? null : accessor, null); } catch { return result; }
            return Values(raw);
        }

        private static object FindInstance(Type type)
        {
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (PropertyInfo p in type.GetProperties(f))
            {
                if (p.GetIndexParameters().Length != 0 || !type.IsAssignableFrom(p.PropertyType)) continue;
                try { object v = p.GetValue(null, null); if (v != null) return v; } catch { }
            }
            foreach (FieldInfo x in type.GetFields(f))
            {
                if (!type.IsAssignableFrom(x.FieldType)) continue;
                try { object v = x.GetValue(null); if (v != null) return v; } catch { }
            }
            foreach (MethodInfo m in type.GetMethods(f))
            {
                if (m.GetParameters().Length != 0 || !type.IsAssignableFrom(m.ReturnType)) continue;
                try { object v = m.Invoke(null, null); if (v != null) return v; } catch { }
            }
            return null;
        }

        private static MethodInfo FindMethod(Type type, string name, int argCount)
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                if (m.Name == name && m.GetParameters().Length == argCount) return m;
            return null;
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type direct = a.GetType(name, false);
                    if (direct != null) return direct;
                    foreach (Type t in a.GetTypes()) if (t.Name == name || t.FullName == name) return t;
                }
                catch { }
            }
            return null;
        }

        private static object GetMemberValue(object instance, string name)
        {
            if (instance == null) return null;
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type t = instance.GetType();
            FieldInfo field = t.GetField(name, f);
            if (field != null) { try { return field.GetValue(instance); } catch { } }
            PropertyInfo prop = t.GetProperty(name, f);
            if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0) { try { return prop.GetValue(instance, null); } catch { } }
            return null;
        }

        private static bool SetMemberValue(object instance, string name, object value)
        {
            if (instance == null) return false;
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type t = instance.GetType();
            FieldInfo field = t.GetField(name, f);
            if (field != null) { try { field.SetValue(instance, value); return true; } catch { } }
            PropertyInfo prop = t.GetProperty(name, f);
            if (prop != null && prop.CanWrite) { try { prop.SetValue(instance, value, null); return true; } catch { } }
            return false;
        }

        private static bool SetBoolMember(object instance, string name, bool value)
        {
            if (instance == null) return false;
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type t = instance.GetType();
            FieldInfo field = t.GetField(name, f);
            if (field != null && field.FieldType == typeof(bool)) { try { field.SetValue(instance, value); return true; } catch { } }
            PropertyInfo prop = t.GetProperty(name, f);
            if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite) { try { prop.SetValue(instance, value, null); return true; } catch { } }
            return false;
        }

        private static List<object> Values(object source)
        {
            var result = new List<object>();
            if (source == null) return result;
            if (source is IDictionary dict)
            {
                foreach (object v in dict.Values) if (v != null) result.Add(v);
                return result;
            }
            if (source is IEnumerable enumerable)
            {
                foreach (object x in enumerable)
                {
                    if (x == null) continue;
                    PropertyInfo vp = x.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (vp != null)
                    {
                        try { object v = vp.GetValue(x, null); if (v != null) result.Add(v); continue; } catch { }
                    }
                    result.Add(x);
                }
            }
            return result;
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}
