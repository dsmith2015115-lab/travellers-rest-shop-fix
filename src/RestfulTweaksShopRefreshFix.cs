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
    [BepInPlugin("dsmith.travellersrest.restfultweaks.shoprefreshfix", "Restful Tweaks Shop Refresh Fix", "1.3.0")]
    [BepInDependency("net.nep.bepinex.restfultweaks", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private static ManualLogSource Log;
        private static GameObject RerollButton;
        private static Component ActiveShopUi;
        private float nextUiScan;
        private static readonly HashSet<string> Logged = new HashSet<string>();
        private static readonly string[] ShopUiTypeNames = { "ShopUI", "FerroShopUI", "AnimalShopUI", "ShopBaseUI" };

        private void Awake()
        {
            Log = Logger;
            try
            {
                MethodBase target = FindShopRefresh();
                if (target != null)
                {
                    new Harmony("dsmith.travellersrest.restfultweaks.shoprefreshfix").Patch(
                        target,
                        prefix: new HarmonyMethod(typeof(Plugin).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    Log.LogInfo("v1.3.0 loaded after Restful Tweaks. Patched " + target.DeclaringType.FullName + "." + target.Name + ".");
                }
                else
                {
                    Log.LogWarning("Restful Tweaks ShopRefresh() was not found. Direct shop UI mode is still enabled.");
                }

                foreach (string typeName in ShopUiTypeNames)
                {
                    Type t = FindType(typeName);
                    Log.LogInfo(t != null ? "Resolved shop UI type: " + t.FullName : "Shop UI type not found: " + typeName);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Startup failed: " + e);
            }
        }

        private void Update()
        {
            if (Time.unscaledTime < nextUiScan) return;
            nextUiScan = Time.unscaledTime + 0.35f;

            try { EnsureShopButton(); }
            catch (Exception e) { Log.LogWarning("Shop UI scan failed: " + Unwrap(e)); }
        }

        private static void EnsureShopButton()
        {
            if (RerollButton != null && RerollButton.activeInHierarchy) return;

            if (RerollButton != null)
            {
                try { Destroy(RerollButton); } catch { }
                RerollButton = null;
                ActiveShopUi = null;
            }

            Component found = null;
            foreach (string typeName in ShopUiTypeNames)
            {
                Type t = FindType(typeName);
                if (t == null || !typeof(Component).IsAssignableFrom(t)) continue;

                UnityEngine.Object[] objects;
                try { objects = Resources.FindObjectsOfTypeAll(t); }
                catch (Exception e)
                {
                    LogOnce("scanerr:" + typeName, "Could not enumerate " + typeName + ": " + e.Message);
                    continue;
                }

                LogOnce("count:" + typeName, "Shop UI scan: " + typeName + " objects found=" + objects.Length + ".");

                foreach (UnityEngine.Object obj in objects)
                {
                    Component c = obj as Component;
                    if (c == null || c.gameObject == null || !c.gameObject.activeInHierarchy) continue;

                    LogOnce("active:" + typeName + ":" + c.gameObject.GetInstanceID(),
                        "Active shop UI detected: " + typeName + " / " + HierarchyPath(c.transform) + ".");
                    found = c;
                    break;
                }

                if (found != null) break;
            }

            if (found == null) return;
            ActiveShopUi = found;

            Component sourceWrapper = FindVersatileButton(found.gameObject);
            GameObject sourceRoot = sourceWrapper != null ? sourceWrapper.gameObject : null;
            Component sourceUnityButton = null;

            if (sourceRoot == null)
            {
                sourceUnityButton = FindUnityButton(found.gameObject);
                sourceRoot = sourceUnityButton != null ? sourceUnityButton.gameObject : null;
            }
            else
            {
                sourceUnityButton = FindUnityButton(sourceRoot);
            }

            if (sourceRoot == null || sourceUnityButton == null)
            {
                LogOnce("nobutton:" + found.gameObject.GetInstanceID(),
                    "Shop UI found, but no VersatileButton/UnityEngine.UI.Button was found under " + HierarchyPath(found.transform) + ".");
                DumpInterestingChildren(found.gameObject);
                return;
            }

            Log.LogInfo("Using shop button template: " + HierarchyPath(sourceRoot.transform) +
                        " (wrapper=" + (sourceWrapper != null ? sourceWrapper.GetType().Name : "none") + ").");

            Transform parent = sourceRoot.transform.parent;
            GameObject clone = Instantiate(sourceRoot, parent);
            clone.name = "ShopRerollButton";

            RectTransform srcRect = sourceRoot.GetComponent<RectTransform>();
            RectTransform cloneRect = clone.GetComponent<RectTransform>();
            if (srcRect != null && cloneRect != null)
            {
                float h = srcRect.rect.height;
                if (h < 1f) h = 42f;
                cloneRect.anchoredPosition = srcRect.anchoredPosition + new Vector2(0f, -(h + 10f));
            }
            else
            {
                clone.transform.localPosition += new Vector3(0f, -55f, 0f);
            }

            Component clonedButton = FindUnityButton(clone);
            if (clonedButton == null)
            {
                Destroy(clone);
                Log.LogError("Cloned shop button did not contain UnityEngine.UI.Button.");
                return;
            }

            RewireClick(clonedButton);
            SetButtonLabel(clone, "Reroll");
            clone.SetActive(true);
            RerollButton = clone;

            Log.LogInfo("Created shop Reroll button at " + HierarchyPath(clone.transform) + ".");
        }

        private static Component FindVersatileButton(GameObject root)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.gameObject.name == "ShopRerollButton") continue;
                if (string.Equals(c.GetType().Name, "VersatileButton", StringComparison.Ordinal)) return c;
            }
            return null;
        }

        private static Component FindUnityButton(GameObject root)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c != null && c.GetType().FullName == "UnityEngine.UI.Button") return c;
            }
            return null;
        }

        private static void RewireClick(Component button)
        {
            PropertyInfo p = button.GetType().GetProperty("onClick", BindingFlags.Public | BindingFlags.Instance);
            if (p == null) throw new MissingMemberException("UnityEngine.UI.Button.onClick not found");
            object evt = p.GetValue(button, null);
            if (evt == null) throw new MissingMemberException("Button.onClick returned null");

            MethodInfo getCount = evt.GetType().GetMethod("GetPersistentEventCount", Type.EmptyTypes);
            MethodInfo setState = null;
            foreach (MethodInfo m in evt.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "SetPersistentListenerState" && m.GetParameters().Length == 2)
                {
                    setState = m;
                    break;
                }
            }

            if (getCount != null && setState != null)
            {
                int count = (int)getCount.Invoke(evt, null);
                Type stateType = setState.GetParameters()[1].ParameterType;
                object off = Enum.ToObject(stateType, 0);
                for (int i = 0; i < count; i++)
                {
                    try { setState.Invoke(evt, new object[] { i, off }); } catch { }
                }
            }

            MethodInfo remove = evt.GetType().GetMethod("RemoveAllListeners", Type.EmptyTypes);
            if (remove != null) remove.Invoke(evt, null);

            MethodInfo add = null;
            foreach (MethodInfo m in evt.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "AddListener" && m.GetParameters().Length == 1) { add = m; break; }
            }
            if (add == null) throw new MissingMethodException("ButtonClickedEvent.AddListener not found");

            Type delegateType = add.GetParameters()[0].ParameterType;
            MethodInfo handler = typeof(Plugin).GetMethod("OnRerollClicked", BindingFlags.NonPublic | BindingFlags.Static);
            Delegate callback = Delegate.CreateDelegate(delegateType, handler);
            add.Invoke(evt, new object[] { callback });
        }

        private static void SetButtonLabel(GameObject button, string label)
        {
            foreach (Component c in button.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string typeName = c.GetType().Name ?? "";
                if (typeName.IndexOf("Localis", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Behaviour b = c as Behaviour;
                    if (b != null) b.enabled = false;
                }

                PropertyInfo text = c.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (text != null && text.CanWrite && text.PropertyType == typeof(string))
                {
                    try { text.SetValue(c, label, null); } catch { }
                }
            }
        }

        private static void OnRerollClicked()
        {
            Log.LogInfo("Shop Reroll button clicked.");
            try
            {
                Refresh();
                TryRefreshVisibleShopUi();
            }
            catch (Exception e)
            {
                Log.LogError("Shop Reroll button failed: " + Unwrap(e));
            }
        }

        private static void TryRefreshVisibleShopUi()
        {
            if (ActiveShopUi == null) return;
            Type t = ActiveShopUi.GetType();
            string[] preferred = { "OpenShopUI", "Refresh", "RefreshUI", "UpdateShop", "UpdateUI" };
            foreach (string name in preferred)
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (m == null) continue;
                try
                {
                    m.Invoke(ActiveShopUi, null);
                    Log.LogInfo("Refreshed visible shop UI via " + t.Name + "." + m.Name + "().");
                    return;
                }
                catch (Exception e)
                {
                    Log.LogWarning("Visible shop UI refresh via " + m.Name + " failed: " + Unwrap(e).Message);
                }
            }
            Log.LogInfo("No known zero-argument visual refresh method found on " + t.Name + "; reopen the shop if stock changed in the database.");
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
            Log.LogInfo("Shop reroll: GetAllShops returned " + shops.Count + " shop record(s).");

            int limited = 0;
            int refreshed = 0;
            foreach (object shop in shops)
            {
                if (shop == null || !Limited(shop)) continue;
                limited++;
                MethodInfo create = FindCreate(accessorType, shop.GetType());
                if (create == null) throw new MissingMethodException("CreateNewShopList not found for " + shop.GetType().FullName);

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

            Log.LogInfo("Shop reroll complete: limited=" + limited + ", refreshed=" + refreshed + ".");
        }

        private static IEnumerable<Assembly> RestfulTweaksAssemblies()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name ?? "";
                if (n.IndexOf("RestfulTweaks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("RestFulTweaks", StringComparison.OrdinalIgnoreCase) >= 0) yield return a;
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
                            m.Name.IndexOf("ShopRefresh", StringComparison.OrdinalIgnoreCase) >= 0) return m;
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
                if (x.PropertyType == typeof(bool) && x.Name.IndexOf("limited", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (bool)x.GetValue(shop, null);
            return false;
        }

        private static List<object> Values(object source)
        {
            List<object> r = new List<object>();
            if (source == null) return r;
            IDictionary d = source as IDictionary;
            if (d != null)
            {
                foreach (object v in d.Values) if (v != null) r.Add(v);
                return r;
            }
            IEnumerable e = source as IEnumerable;
            if (e != null)
            {
                foreach (object x in e)
                {
                    if (x == null) continue;
                    PropertyInfo vp = x.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (vp != null)
                    {
                        try { object v = vp.GetValue(x, null); if (v != null) r.Add(v); continue; } catch { }
                    }
                    r.Add(x);
                }
            }
            return r;
        }

        private static void DumpInterestingChildren(GameObject root)
        {
            int shown = 0;
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string tn = c.GetType().Name ?? "";
                if (tn.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0 &&
                    tn.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Log.LogInfo("Shop child component: " + tn + " @ " + HierarchyPath(c.transform));
                if (++shown >= 30) break;
            }
        }

        private static string HierarchyPath(Transform t)
        {
            if (t == null) return "<null>";
            string path = t.name;
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 12)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }

        private static void LogOnce(string key, string text)
        {
            if (Logged.Add(key)) Log.LogInfo(text);
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}
