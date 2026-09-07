using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TravellersRest.ShopRefreshFix
{
    [BepInPlugin("dsmith.travellersrest.restfultweaks.shoprefreshfix", "Restful Tweaks Shop Compatibility", "2.0.1")]
    [BepInDependency("net.nep.bepinex.restfultweaks", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string HarmonyId = "dsmith.travellersrest.restfultweaks.shoprefreshfix";
        private static ManualLogSource Log;
        private static Type RestfulPluginType;
        private static bool DatabaseReady;
        private static bool ConfigInitialized;
        private static bool LastDaily;
        private static bool LastAllItems;
        private static bool LastUnlimited;
        private float nextConfigCheck;

        private static readonly Dictionary<object, ShopSnapshot> ShopSnapshots =
            new Dictionary<object, ShopSnapshot>(ReferenceComparer.Instance);
        private static readonly Dictionary<object, ItemSnapshot> ItemSnapshots =
            new Dictionary<object, ItemSnapshot>(ReferenceComparer.Instance);

        private void Awake()
        {
            Log = Logger;
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                RestfulPluginType = FindRestfulPluginType();

                MethodBase oldRefresh = FindRestfulShopRefresh();
                if (oldRefresh != null)
                {
                    harmony.Patch(oldRefresh,
                        prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ShopRefreshPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    Log.LogInfo("Replaced deprecated Restful Tweaks ShopRefresh backend.");
                }
                else
                {
                    Log.LogWarning("Restful Tweaks ShopRefresh() was not found; config compatibility remains active.");
                }

                Type accessorType = FindType("ShopDatabaseAccessor");
                MethodInfo awake = accessorType != null ? FindZeroArgMethod(accessorType, "Awake") : null;
                if (awake != null)
                {
                    harmony.Patch(awake,
                        postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ShopDatabaseAwakePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                    Log.LogInfo("Hooked current ShopDatabaseAccessor.Awake().");
                }
                else
                {
                    Log.LogWarning("Current ShopDatabaseAccessor.Awake() was not found.");
                }

                // Important: do NOT touch GetAllShops here. The game database does not exist yet.
                Log.LogInfo("Shop compatibility v2.0.1 loaded; waiting for ShopDatabaseAccessor.Awake() before reading shop data.");
            }
            catch (Exception e)
            {
                Log.LogError("Shop compatibility startup failed: " + Unwrap(e));
            }
        }

        private void Update()
        {
            if (!DatabaseReady || Time.unscaledTime < nextConfigCheck)
                return;

            nextConfigCheck = Time.unscaledTime + 0.75f;
            try
            {
                ReadAndApplyConfig(true);
            }
            catch (Exception e)
            {
                Log.LogWarning("Shop config check failed: " + Unwrap(e).Message);
            }
        }

        private static void ShopDatabaseAwakePostfix()
        {
            DatabaseReady = true;
            ConfigInitialized = false;
            ShopSnapshots.Clear();
            ItemSnapshots.Clear();

            try
            {
                Log.LogInfo("Shop database initialized; applying Restful Tweaks shop settings now.");
                ReadAndApplyConfig(false);
            }
            catch (Exception e)
            {
                Log.LogError("Applying shop settings after database initialization failed: " + Unwrap(e));
            }
        }

        private static bool ShopRefreshPrefix()
        {
            if (!DatabaseReady)
            {
                Log.LogWarning("Refresh Shops requested before the shop database was ready; request ignored safely.");
                return false;
            }

            try
            {
                ReadAndApplyConfig(false);
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
            if (!DatabaseReady)
                return;

            if (RestfulPluginType == null)
                RestfulPluginType = FindRestfulPluginType();
            if (RestfulPluginType == null)
                return;

            bool daily = ReadConfigBool("_shopUpdateDaily");
            bool allItems = ReadConfigBool("_shopAllItems");
            bool unlimited = ReadConfigBool("_shopMoreItems");

            bool changed = !ConfigInitialized || daily != LastDaily || allItems != LastAllItems || unlimited != LastUnlimited;
            if (!changed)
                return;

            bool first = !ConfigInitialized;
            LastDaily = daily;
            LastAllItems = allItems;
            LastUnlimited = unlimited;
            ConfigInitialized = true;

            ApplySettingsToAllShops();
            Log.LogInfo("Restful Tweaks shop settings applied: Update Stock Daily=" + daily +
                        ", All Items=" + allItems + ", Unlimited Items=" + unlimited + ".");

            if (refreshOnChange && !first)
            {
                RefreshAllLimitedShops();
                Log.LogInfo("Shop setting changed; regenerated limited shop inventories.");
            }
        }

        private static bool ReadConfigBool(string fieldName)
        {
            FieldInfo field = RestfulPluginType.GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (field == null) return false;

            object entry = field.GetValue(null);
            if (entry == null) return false;

            PropertyInfo value = entry.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (value != null && value.PropertyType == typeof(bool))
                return (bool)value.GetValue(entry, null);

            PropertyInfo boxed = entry.GetType().GetProperty("BoxedValue", BindingFlags.Public | BindingFlags.Instance);
            if (boxed != null)
            {
                object v = boxed.GetValue(entry, null);
                if (v is bool) return (bool)v;
            }
            return false;
        }

        private static void ApplySettingsToAllShops()
        {
            List<object> shops = GetAllShops();
            int changedShops = 0;
            int changedItems = 0;

            foreach (object shop in shops)
            {
                if (shop == null) continue;

                CaptureShopSnapshot(shop);
                if (ApplyDailySetting(shop, LastDaily)) changedShops++;

                object items = GetMemberValue(shop, "shopItems");
                if (!(items is IEnumerable enumerable)) continue;

                foreach (object item in enumerable)
                {
                    if (item == null) continue;
                    CaptureItemSnapshot(item);
                    bool changed = false;
                    changed |= SetBoolMember(item, "alwaysAppear", LastAllItems ? true : ItemSnapshots[item].AlwaysAppear);
                    changed |= SetBoolMember(item, "unlimited", LastUnlimited ? true : ItemSnapshots[item].Unlimited);
                    if (changed) changedItems++;
                }
            }

            Log.LogInfo("Applied current shop settings to " + shops.Count + " shop record(s); changed shop records=" +
                        changedShops + ", changed item records=" + changedItems + ".");
        }

        private static bool ApplyDailySetting(object shop, bool enabled)
        {
            object current = GetMemberValue(shop, "updateDays");
            if (current == null) return false;

            object replacement = enabled ? CreateEveryDayList(current.GetType()) : CloneList(ShopSnapshots[shop].UpdateDays);
            if (replacement == null) return false;
            return SetMemberValue(shop, "updateDays", replacement);
        }

        private static object CreateEveryDayList(Type listType)
        {
            try
            {
                object list = Activator.CreateInstance(listType);
                IList ilist = list as IList;
                if (ilist == null) return null;

                Type[] args = listType.IsGenericType ? listType.GetGenericArguments() : Type.EmptyTypes;
                if (args.Length != 1 || !args[0].IsEnum) return null;

                foreach (string day in new[] { "Mon", "Tue", "Wed", "Thurs", "Fri", "Sat", "Sun" })
                {
                    try { ilist.Add(Enum.Parse(args[0], day, true)); } catch { }
                }
                return list;
            }
            catch { return null; }
        }

        private static void CaptureShopSnapshot(object shop)
        {
            if (ShopSnapshots.ContainsKey(shop)) return;
            ShopSnapshots[shop] = new ShopSnapshot { UpdateDays = CloneList(GetMemberValue(shop, "updateDays")) };
        }

        private static void CaptureItemSnapshot(object item)
        {
            if (ItemSnapshots.ContainsKey(item)) return;
            ItemSnapshots[item] = new ItemSnapshot
            {
                AlwaysAppear = ReadBoolMember(item, "alwaysAppear"),
                Unlimited = ReadBoolMember(item, "unlimited")
            };
        }

        private static object CloneList(object source)
        {
            if (!(source is IEnumerable enumerable)) return source;
            try
            {
                object clone = Activator.CreateInstance(source.GetType());
                IList list = clone as IList;
                if (list == null) return source;
                foreach (object value in enumerable) list.Add(value);
                return clone;
            }
            catch { return source; }
        }

        private static void RefreshAllLimitedShops()
        {
            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null) throw new MissingMemberException("ShopDatabaseAccessor not found");

            object accessor = FindInstance(accessorType);
            List<object> shops = GetAllShops();
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

            Log.LogInfo("Modern shop refresh complete: refreshed " + refreshed + "/" + limited + " limited shop(s).");
        }

        private static List<object> GetAllShops()
        {
            if (!DatabaseReady) return new List<object>();

            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null) return new List<object>();

            object accessor = FindInstance(accessorType);
            MethodInfo getAll = FindZeroArgMethod(accessorType, "GetAllShops");
            if (getAll == null) return new List<object>();

            object result = getAll.Invoke(getAll.IsStatic ? null : accessor, null);
            return Values(result);
        }

        private static MethodInfo FindCreateNewShopList(Type accessorType, Type shopType)
        {
            MethodInfo best = null;
            foreach (MethodInfo method in accessorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (method.Name != "CreateNewShopList") continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 0) continue;
                if (!p[0].ParameterType.IsAssignableFrom(shopType) && !shopType.IsAssignableFrom(p[0].ParameterType)) continue;
                if (best == null || p.Length < best.GetParameters().Length) best = method;
            }
            return best;
        }

        private static object[] BuildArguments(MethodInfo method, object shop)
        {
            ParameterInfo[] p = method.GetParameters();
            object[] args = new object[p.Length];
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
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (PropertyInfo p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length != 0 || !type.IsAssignableFrom(p.PropertyType)) continue;
                try { object v = p.GetValue(null, null); if (v != null) return v; } catch { }
            }
            foreach (FieldInfo f in type.GetFields(flags))
            {
                if (!type.IsAssignableFrom(f.FieldType)) continue;
                try { object v = f.GetValue(null); if (v != null) return v; } catch { }
            }
            foreach (MethodInfo m in type.GetMethods(flags))
            {
                if (m.GetParameters().Length != 0 || !type.IsAssignableFrom(m.ReturnType)) continue;
                try { object v = m.Invoke(null, null); if (v != null) return v; } catch { }
            }
            return null;
        }

        private static object GetMemberValue(object instance, string name)
        {
            if (instance == null) return null;
            Type t = instance.GetType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo f = t.GetField(name, flags);
            if (f != null) { try { return f.GetValue(instance); } catch { } }
            PropertyInfo p = t.GetProperty(name, flags);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
            { try { return p.GetValue(instance, null); } catch { } }
            return null;
        }

        private static bool SetMemberValue(object instance, string name, object value)
        {
            Type t = instance.GetType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo f = t.GetField(name, flags);
            if (f != null)
            {
                try
                {
                    object current = f.GetValue(instance);
                    if (Equals(current, value)) return false;
                    f.SetValue(instance, value);
                    return true;
                }
                catch { }
            }
            PropertyInfo p = t.GetProperty(name, flags);
            if (p != null && p.CanWrite)
            {
                try
                {
                    object current = p.CanRead ? p.GetValue(instance, null) : null;
                    if (Equals(current, value)) return false;
                    p.SetValue(instance, value, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static bool ReadBoolMember(object instance, string name)
        {
            object v = GetMemberValue(instance, name);
            return v is bool && (bool)v;
        }

        private static bool SetBoolMember(object instance, string name, bool value)
        {
            Type t = instance.GetType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo f = t.GetField(name, flags);
            if (f != null && f.FieldType == typeof(bool))
            {
                try
                {
                    bool current = (bool)f.GetValue(instance);
                    if (current == value) return false;
                    f.SetValue(instance, value);
                    return true;
                }
                catch { }
            }
            PropertyInfo p = t.GetProperty(name, flags);
            if (p != null && p.PropertyType == typeof(bool) && p.CanWrite)
            {
                try
                {
                    bool current = p.CanRead && (bool)p.GetValue(instance, null);
                    if (current == value) return false;
                    p.SetValue(instance, value, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static MethodInfo FindZeroArgMethod(Type type, string name)
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                if (m.Name == name && m.GetParameters().Length == 0) return m;
            return null;
        }

        private static Type FindRestfulPluginType()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name ?? "";
                if (n.IndexOf("RestfulTweaks", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("RestFulTweaks", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    Type direct = a.GetType("RestfulTweaks.Plugin", false);
                    if (direct != null) return direct;
                    foreach (Type t in a.GetTypes())
                        if (t.Name == "Plugin" && t.Namespace == "RestfulTweaks") return t;
                }
                catch { }
            }
            return null;
        }

        private static MethodBase FindRestfulShopRefresh()
        {
            Type t = RestfulPluginType ?? FindRestfulPluginType();
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                if (m.Name == "ShopRefresh" && m.ReturnType == typeof(void) && m.GetParameters().Length == 0) return m;
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
                    foreach (Type t in a.GetTypes()) if (t.Name == name) return t;
                }
                catch { }
            }
            return null;
        }

        private static List<object> Values(object source)
        {
            List<object> result = new List<object>();
            if (source == null) return result;
            if (source is IDictionary dictionary)
            {
                foreach (object value in dictionary.Values) if (value != null) result.Add(value);
                return result;
            }
            if (source is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    if (item == null) continue;
                    PropertyInfo p = item.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (p != null)
                    {
                        try
                        {
                            object value = p.GetValue(item, null);
                            if (value != null) result.Add(value);
                            continue;
                        }
                        catch { }
                    }
                    result.Add(item);
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
