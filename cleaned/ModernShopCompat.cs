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
        private static readonly ManualLogSource Log = Logger.CreateLogSource("Restful Tweaks Redux Clean");
        private static readonly Dictionary<object, ShopSnapshot> ShopSnapshots = new Dictionary<object, ShopSnapshot>(ReferenceComparer.Instance);
        private static readonly Dictionary<object, ItemSnapshot> ItemSnapshots = new Dictionary<object, ItemSnapshot>(ReferenceComparer.Instance);
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
                    Log.LogInfo("Replaced deprecated ShopRefresh backend in-place.");
                }

                var accessor = FindType("ShopDatabaseAccessor");
                var accessorAwake = accessor != null ? FindMethod(accessor, "Awake", 0) : null;
                if (accessorAwake != null)
                {
                    harmony.Patch(accessorAwake, postfix: new HarmonyMethod(typeof(ModernShopCompat).GetMethod(nameof(ShopDatabaseAwakePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                    Log.LogInfo("Hooked current ShopDatabaseAccessor.Awake for deferred shop initialization.");
                }

                var update = pluginType != null ? FindMethod(pluginType, "Update", 0) : null;
                if (update != null)
                {
                    harmony.Patch(update, postfix: new HarmonyMethod(typeof(ModernShopCompat).GetMethod(nameof(PluginUpdatePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                Log.LogInfo("Cleaned shop compatibility initialized. No UI scans or vendor lifecycle hooks.");
            }
            catch (Exception e)
            {
                Log.LogError("Modern compatibility initialization failed: " + Unwrap(e));
            }
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
                RefreshAllLimitedShops();
            }
            catch (Exception e)
            {
                Log.LogError("Modern shop refresh failed: " + Unwrap(e));
            }
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

            if (refreshOnChange && !first)
            {
                RefreshAllLimitedShops();
                Log.LogInfo("Shop setting changed; regenerated limited inventories.");
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
            int changedShops = 0;
            int changedItems = 0;

            foreach (object shop in shops)
            {
                if (shop == null) continue;
                CaptureShopSnapshot(shop);
                if (ApplyDailySetting(shop, lastDaily)) changedShops++;

                object items = GetMemberValue(shop, "shopItems");
                var enumerable = items as IEnumerable;
                if (enumerable == null) continue;

                foreach (object item in enumerable)
                {
                    if (item == null) continue;
                    CaptureItemSnapshot(item);
                    bool changed = false;
                    changed |= SetBoolMember(item, "alwaysAppear", lastAllItems ? true : ItemSnapshots[item].AlwaysAppear);
                    changed |= SetBoolMember(item, "unlimited", lastUnlimited ? true : ItemSnapshots[item].Unlimited);
                    if (changed) changedItems++;
                }
            }

            Log.LogInfo("Applied shop settings to " + shops.Count + " shop record(s); changed shops=" + changedShops + ", changed items=" + changedItems + ".");
        }

        private static bool ApplyDailySetting(object shop, bool enabled)
        {
            ShopSnapshot snapshot = ShopSnapshots[shop];
            object current = GetMemberValue(shop, "updateDays");
            if (current == null) return false;
            object replacement = enabled ? CreateEveryDayList(current.GetType()) : CloneList(snapshot.UpdateDays);
            if (replacement == null) return false;
            return SetMemberValue(shop, "updateDays", replacement);
        }

        private static object CreateEveryDayList(Type listType)
        {
            try
            {
                object list = Activator.CreateInstance(listType);
                IList ilist = list as IList;
                if (ilist == null || !listType.IsGenericType) return null;
                Type dayType = listType.GetGenericArguments()[0];
                if (!dayType.IsEnum) return null;
                string[] days = { "Mon", "Tue", "Wed", "Thurs", "Fri", "Sat", "Sun" };
                foreach (string day in days)
                {
                    try { ilist.Add(Enum.Parse(dayType, day, true)); } catch { }
                }
                return list;
            }
            catch { return null; }
        }

        private static void CaptureShopSnapshot(object shop)
        {
            if (!ShopSnapshots.ContainsKey(shop)) ShopSnapshots[shop] = new ShopSnapshot { UpdateDays = CloneList(GetMemberValue(shop, "updateDays")) };
        }

        private static void CaptureItemSnapshot(object item)
        {
            if (!ItemSnapshots.ContainsKey(item)) ItemSnapshots[item] = new ItemSnapshot { AlwaysAppear = ReadBoolMember(item, "alwaysAppear"), Unlimited = ReadBoolMember(item, "unlimited") };
        }

        private static object CloneList(object source)
        {
            if (!(source is IEnumerable)) return source;
            try
            {
                object clone = Activator.CreateInstance(source.GetType());
                IList list = clone as IList;
                if (list == null) return source;
                foreach (object value in (IEnumerable)source) list.Add(value);
                return clone;
            }
            catch { return source; }
        }

        private static void RefreshAllLimitedShops()
        {
            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null) throw new MissingMemberException("ShopDatabaseAccessor not found");
            object accessor = FindInstance(accessorType);
            var shops = GetAllShops();
            int limited = 0;
            int refreshed = 0;

            foreach (object shop in shops)
            {
                if (shop == null || !ReadBoolMember(shop, "limitedItems")) continue;
                limited++;
                MethodInfo create = FindCreateNewShopList(accessorType, shop.GetType());
                if (create == null)
                {
                    Log.LogWarning("No compatible CreateNewShopList overload for " + shop.GetType().FullName + ".");
                    continue;
                }
                create.Invoke(create.IsStatic ? null : accessor, BuildArguments(create, shop));
                refreshed++;
            }
            Log.LogInfo("Shop refresh complete: refreshed " + refreshed + "/" + limited + " limited shop(s).");
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
            try { raw = getAll.Invoke(getAll.IsStatic ? null : accessor, null); }
            catch { return result; }
            return Values(raw);
        }

        private static MethodInfo FindCreateNewShopList(Type accessorType, Type shopType)
        {
            MethodInfo best = null;
            foreach (MethodInfo method in accessorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (method.Name != "CreateNewShopList") continue;
                var p = method.GetParameters();
                if (p.Length == 0) continue;
                Type first = p[0].ParameterType;
                if (!first.IsAssignableFrom(shopType) && !shopType.IsAssignableFrom(first)) continue;
                if (best == null || p.Length < best.GetParameters().Length) best = method;
            }
            return best;
        }

        private static object[] BuildArguments(MethodInfo method, object shop)
        {
            var p = method.GetParameters();
            var args = new object[p.Length];
            args[0] = shop;
            for (int i = 1; i < p.Length; i++)
            {
                if (p[i].HasDefaultValue) args[i] = p[i].DefaultValue;
                else if (p[i].ParameterType == typeof(bool)) args[i] = false;
                else if (p[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(p[i].ParameterType);
                else args[i] = null;
            }
            return args;
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

        private static bool ReadBoolMember(object instance, string name)
        {
            object v = GetMemberValue(instance, name);
            return v is bool && (bool)v;
        }

        private static bool SetBoolMember(object instance, string name, bool value)
        {
            if (instance == null) return false;
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type t = instance.GetType();
            FieldInfo field = t.GetField(name, f);
            if (field != null && field.FieldType == typeof(bool))
            {
                try { bool old = (bool)field.GetValue(instance); if (old == value) return false; field.SetValue(instance, value); return true; } catch { }
            }
            PropertyInfo prop = t.GetProperty(name, f);
            if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
            {
                try { bool old = prop.CanRead && (bool)prop.GetValue(instance, null); if (old == value) return false; prop.SetValue(instance, value, null); return true; } catch { }
            }
            return false;
        }

        private static List<object> Values(object source)
        {
            var result = new List<object>();
            if (source == null) return result;
            if (source is IDictionary)
            {
                foreach (object v in ((IDictionary)source).Values) if (v != null) result.Add(v);
                return result;
            }
            if (source is IEnumerable)
            {
                foreach (object x in (IEnumerable)source)
                {
                    if (x == null) continue;
                    var vp = x.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
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

        private sealed class ShopSnapshot { public object UpdateDays; }
        private sealed class ItemSnapshot { public bool AlwaysAppear; public bool Unlimited; }
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
