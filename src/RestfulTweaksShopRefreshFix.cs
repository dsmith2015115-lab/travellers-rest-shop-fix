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
    [BepInPlugin("dsmith.travellersrest.restfultweaks.shoprefreshfix", "Restful Tweaks Shop Refresh Fix", "1.5.0")]
    [BepInDependency("net.nep.bepinex.restfultweaks", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string HarmonyId = "dsmith.travellersrest.restfultweaks.shoprefreshfix";
        private static ManualLogSource Log;
        private static bool dumpingCandidates;

        private void Awake()
        {
            Log = Logger;
            try
            {
                MethodBase target = FindShopRefresh();
                if (target != null)
                {
                    new Harmony(HarmonyId).Patch(
                        target,
                        prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    Log.LogInfo("v1.5.0 loaded. Patched " + target.DeclaringType.FullName + "." + target.Name + ".");
                }
                else
                {
                    Log.LogWarning("Restful Tweaks ShopRefresh() was not found. F8 seed-shop diagnostic is still available.");
                }

                Log.LogInfo("Safe mode active: NO vendor OpenShopUI lifecycle hooks. Open the seed shop and press F8.");
            }
            catch (Exception e)
            {
                Log.LogError("Startup failed: " + e);
            }
        }

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    Log.LogInfo("F8 pressed: attempting seed/farm shop reroll.");
                    RefreshSeedShopOnly();
                }
            }
            catch (Exception e)
            {
                Log.LogError("F8 seed-shop reroll failed: " + Unwrap(e));
            }
        }

        private static bool Prefix()
        {
            try
            {
                RefreshSeedShopOnly();
            }
            catch (Exception e)
            {
                Log.LogError("Restful Tweaks ShopRefresh redirect failed: " + Unwrap(e));
            }
            return false;
        }

        private static void RefreshSeedShopOnly()
        {
            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null)
                throw new MissingMemberException("ShopDatabaseAccessor not found");

            object accessor = FindInstance(accessorType);
            MethodInfo getAll = FindMethod(accessorType, "GetAllShops", 0);
            if (getAll == null)
                throw new MissingMethodException("ShopDatabaseAccessor.GetAllShops() not found");

            List<object> shops = Values(getAll.Invoke(getAll.IsStatic ? null : accessor, null));
            Log.LogInfo("Seed reroll scan: GetAllShops returned " + shops.Count + " record(s).");

            object best = null;
            int bestScore = 0;
            string bestDesc = "";

            foreach (object shop in shops)
            {
                if (shop == null)
                    continue;

                string desc;
                int score = SeedShopScore(shop, out desc);
                bool limited = Limited(shop);

                if (score > 0 || dumpingCandidates)
                    Log.LogInfo("Shop candidate: score=" + score + ", limited=" + limited + ", " + desc);

                if (!limited || score <= bestScore)
                    continue;

                best = shop;
                bestScore = score;
                bestDesc = desc;
            }

            if (best == null)
            {
                Log.LogWarning("No limited seed/farm/crop shop candidate was identified. Dumping all shop candidates once.");
                dumpingCandidates = true;
                foreach (object shop in shops)
                {
                    if (shop == null) continue;
                    string desc;
                    int score = SeedShopScore(shop, out desc);
                    Log.LogInfo("Shop candidate: score=" + score + ", limited=" + Limited(shop) + ", " + desc);
                }
                dumpingCandidates = false;
                return;
            }

            MethodInfo create = FindCreate(accessorType, best.GetType());
            if (create == null)
                throw new MissingMethodException("CreateNewShopList not found for " + best.GetType().FullName);

            ParameterInfo[] p = create.GetParameters();
            object[] args = new object[p.Length];
            args[0] = best;
            for (int i = 1; i < p.Length; i++)
            {
                if (p[i].HasDefaultValue) args[i] = p[i].DefaultValue;
                else if (p[i].ParameterType == typeof(bool)) args[i] = false;
                else if (p[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(p[i].ParameterType);
                else args[i] = null;
            }

            Log.LogInfo("Refreshing selected seed-shop candidate: score=" + bestScore + ", " + bestDesc);
            create.Invoke(create.IsStatic ? null : accessor, args);
            Log.LogInfo("Seed-shop database reroll completed. Close/reopen the shop if the visible list does not update immediately.");

            TrySafeVisibleRefresh();
        }

        private static int SeedShopScore(object shop, out string description)
        {
            Type t = shop.GetType();
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            int score = 0;
            List<string> clues = new List<string>();

            Action<string, object> inspect = (name, value) =>
            {
                if (value == null) return;
                string s = value as string;
                if (s == null)
                {
                    if (value is UnityEngine.Object uo) s = uo.name;
                    else if (value.GetType().IsEnum) s = value.ToString();
                    else return;
                }
                if (string.IsNullOrEmpty(s)) return;

                string hay = (name + " " + s).ToLowerInvariant();
                int local = 0;
                if (hay.Contains("seed")) local += 12;
                if (hay.Contains("farm")) local += 8;
                if (hay.Contains("crop")) local += 7;
                if (hay.Contains("plant")) local += 4;
                if (hay.Contains("garden")) local += 3;
                if (local > 0)
                {
                    score += local;
                    clues.Add(name + "=" + s);
                }
            };

            foreach (FieldInfo x in t.GetFields(f))
            {
                try { inspect(x.Name, x.GetValue(shop)); } catch { }
            }

            foreach (PropertyInfo x in t.GetProperties(f))
            {
                if (x.GetIndexParameters().Length != 0 || !x.CanRead) continue;
                try { inspect(x.Name, x.GetValue(shop, null)); } catch { }
            }

            string typeName = t.FullName ?? t.Name;
            string lowerType = typeName.ToLowerInvariant();
            if (lowerType.Contains("seed")) score += 12;
            if (lowerType.Contains("farm")) score += 8;
            if (lowerType.Contains("crop")) score += 7;

            description = "type=" + typeName + (clues.Count > 0 ? ", clues=[" + string.Join(", ", clues.ToArray()) + "]" : "");
            return score;
        }

        private static void TrySafeVisibleRefresh()
        {
            string[] typeNames = { "ShopUI", "ShopBaseUI" };
            string[] methodNames = { "Refresh", "RefreshUI", "UpdateUI", "UpdateShop" };

            foreach (string typeName in typeNames)
            {
                Type t = FindType(typeName);
                if (t == null || !typeof(Component).IsAssignableFrom(t)) continue;

                UnityEngine.Object[] objects;
                try { objects = Resources.FindObjectsOfTypeAll(t); }
                catch { continue; }

                foreach (UnityEngine.Object obj in objects)
                {
                    Component c = obj as Component;
                    if (c == null || c.gameObject == null || !c.gameObject.activeInHierarchy) continue;

                    foreach (string methodName in methodNames)
                    {
                        MethodInfo m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                            null, Type.EmptyTypes, null);
                        if (m == null) continue;
                        try
                        {
                            m.Invoke(c, null);
                            Log.LogInfo("Safely refreshed visible shop UI via " + t.Name + "." + m.Name + "().");
                            return;
                        }
                        catch (Exception e)
                        {
                            Log.LogWarning("Visible shop UI refresh via " + t.Name + "." + m.Name + " failed: " + Unwrap(e).Message);
                        }
                    }
                }
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
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                        if (m.ReturnType == typeof(void) && m.GetParameters().Length == 0 &&
                            m.Name.IndexOf("ShopRefresh", StringComparison.OrdinalIgnoreCase) >= 0)
                            return m;
                }
                catch { }
            }
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
            {
                if (!t.IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length != 0) continue;
                try { object v = p.GetValue(null, null); if (v != null) return v; } catch { }
            }
            foreach (FieldInfo x in t.GetFields(f))
            {
                if (!t.IsAssignableFrom(x.FieldType)) continue;
                try { object v = x.GetValue(null); if (v != null) return v; } catch { }
            }
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

            foreach (FieldInfo x in t.GetFields(f))
                if (x.FieldType == typeof(bool) && x.Name.IndexOf("limited", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (bool)x.GetValue(shop);

            foreach (PropertyInfo x in t.GetProperties(f))
                if (x.PropertyType == typeof(bool) && x.GetIndexParameters().Length == 0 &&
                    x.Name.IndexOf("limited", StringComparison.OrdinalIgnoreCase) >= 0)
                    try { return (bool)x.GetValue(shop, null); } catch { }

            return false;
        }

        private static List<object> Values(object source)
        {
            List<object> r = new List<object>();
            if (source == null) return r;

            if (source is IDictionary d)
            {
                foreach (object v in d.Values) if (v != null) r.Add(v);
                return r;
            }

            if (source is IEnumerable e)
            {
                foreach (object x in e)
                {
                    if (x == null) continue;
                    PropertyInfo vp = x.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (vp != null)
                    {
                        try
                        {
                            object v = vp.GetValue(x, null);
                            if (v != null) r.Add(v);
                            continue;
                        }
                        catch { }
                    }
                    r.Add(x);
                }
            }
            return r;
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null)
                e = e.InnerException;
            return e;
        }
    }
}
