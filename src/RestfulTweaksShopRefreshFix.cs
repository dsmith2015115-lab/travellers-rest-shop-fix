using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace TravellersRest.ShopRefreshFix
{
    [BepInPlugin("dsmith.travellersrest.restfultweaks.shoprefreshfix", "Restful Tweaks Shop Refresh Fix", "1.1.0")]
    [BepInDependency("net.nep.bepinex.restfultweaks", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            try
            {
                MethodBase target = FindShopRefresh();
                if (target == null)
                {
                    Log.LogError("Restful Tweaks is loaded, but ShopRefresh() still was not found. Dumping candidate methods.");
                    DumpShopCandidates();
                    return;
                }

                new Harmony("dsmith.travellersrest.restfultweaks.shoprefreshfix").Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(Plugin).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));

                Log.LogInfo("Shop refresh compatibility fix loaded after Restful Tweaks. Patched " +
                    target.DeclaringType.FullName + "." + target.Name + ".");
            }
            catch (Exception e)
            {
                Log.LogError("Failed to install shop refresh compatibility patch: " + e);
            }
        }

        private static IEnumerable<Assembly> RestfulTweaksAssemblies()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name ?? "";
                if (n.IndexOf("RestfulTweaks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("RestFulTweaks", StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return a;
            }
        }

        private static MethodBase FindShopRefresh()
        {
            foreach (Assembly a in RestfulTweaksAssemblies())
            {
                try
                {
                    foreach (Type t in a.GetTypes())
                    {
                        foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                        {
                            if (m.ReturnType != typeof(void) || m.GetParameters().Length != 0)
                                continue;

                            if (string.Equals(m.Name, "ShopRefresh", StringComparison.OrdinalIgnoreCase) ||
                                m.Name.IndexOf("ShopRefresh", StringComparison.OrdinalIgnoreCase) >= 0)
                                return m;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning("Could not scan Restful Tweaks assembly " + a.GetName().Name + ": " + e.Message);
                }
            }
            return null;
        }

        private static void DumpShopCandidates()
        {
            foreach (Assembly a in RestfulTweaksAssemblies())
            {
                try
                {
                    foreach (Type t in a.GetTypes())
                    {
                        foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                        {
                            string full = (t.FullName ?? t.Name) + "." + m.Name;
                            if (full.IndexOf("shop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                full.IndexOf("refresh", StringComparison.OrdinalIgnoreCase) >= 0)
                                Log.LogInfo("Shop candidate: " + full + " (params=" + m.GetParameters().Length + ", return=" + m.ReturnType.Name + ")");
                        }
                    }
                }
                catch { }
            }
        }

        private static bool Prefix()
        {
            try { Refresh(); }
            catch (Exception e) { Log.LogError("Shop reroll failed: " + Unwrap(e)); }
            return false;
        }

        private static void Refresh()
        {
            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null) throw new MissingMemberException("ShopDatabaseAccessor not found");
            object accessor = FindInstance(accessorType);
            MethodInfo getAll = FindMethod(accessorType, "GetAllShops", 0);
            if (getAll == null) throw new MissingMethodException("GetAllShops not found");
            List<object> shops = Values(getAll.Invoke(getAll.IsStatic ? null : accessor, null));
            int refreshed = 0;
            foreach (object shop in shops)
            {
                if (shop == null || !Limited(shop)) continue;
                MethodInfo create = FindCreate(accessorType, shop.GetType());
                if (create == null) throw new MissingMethodException("CreateNewShopList not found");
                ParameterInfo[] p = create.GetParameters();
                object[] args = new object[p.Length];
                args[0] = shop;
                for (int i = 1; i < p.Length; i++)
                {
                    if (p[i].HasDefaultValue) args[i] = p[i].DefaultValue;
                    else if (p[i].ParameterType == typeof(bool)) args[i] = false;
                    else if (p[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(p[i].ParameterType);
                    else args[i] = null;
                }
                create.Invoke(create.IsStatic ? null : accessor, args);
                refreshed++;
            }
            Log.LogInfo("Shop reroll complete: refreshed " + refreshed + " limited shop(s).");
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { foreach (Type t in a.GetTypes()) if (t.Name == name) return t; }
                catch { }
            }
            return null;
        }

        private static MethodInfo FindMethod(Type t, string name, int count)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                if (m.Name == name && m.GetParameters().Length == count) return m;
            return null;
        }

        private static MethodInfo FindCreate(Type t, Type shopType)
        {
            MethodInfo best = null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.Name != "CreateNewShopList") continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 0 || !p[0].ParameterType.IsAssignableFrom(shopType)) continue;
                if (best == null || p.Length < best.GetParameters().Length) best = m;
            }
            return best;
        }

        private static object FindInstance(Type t)
        {
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (PropertyInfo p in t.GetProperties(f))
                if (t.IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0)
                    try { object v = p.GetValue(null, null); if (v != null) return v; } catch { }
            foreach (FieldInfo x in t.GetFields(f))
                if (t.IsAssignableFrom(x.FieldType))
                    try { object v = x.GetValue(null); if (v != null) return v; } catch { }
            return null;
        }

        private static bool Limited(object shop)
        {
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type t = shop.GetType();
            FieldInfo field = t.GetField("limitedItems", f);
            if (field != null && field.FieldType == typeof(bool)) return (bool)field.GetValue(shop);
            PropertyInfo prop = t.GetProperty("limitedItems", f);
            if (prop != null && prop.PropertyType == typeof(bool)) return (bool)prop.GetValue(shop, null);
            return false;
        }

        private static List<object> Values(object source)
        {
            List<object> r = new List<object>();
            IDictionary d = source as IDictionary;
            if (d != null) { foreach (object v in d.Values) if (v != null) r.Add(v); return r; }
            IEnumerable e = source as IEnumerable;
            if (e != null) foreach (object x in e) if (x != null) r.Add(x);
            return r;
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}
