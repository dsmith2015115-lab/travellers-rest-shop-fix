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
    [BepInPlugin("dsmith.travellersrest.restfultweaks.shoprefreshfix", "Restful Tweaks Shop Refresh Fix", "1.2.0")]
    [BepInDependency("net.nep.bepinex.restfultweaks", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private static ManualLogSource Log;
        private static GameObject RerollButton;
        private float nextUiScan;
        private static readonly HashSet<string> LoggedShopCandidates = new HashSet<string>();

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

                    Log.LogInfo("v1.2.0 loaded after Restful Tweaks. Patched " +
                        target.DeclaringType.FullName + "." + target.Name + ".");
                }
                else
                {
                    Log.LogWarning("Restful Tweaks ShopRefresh() was not found. UI button mode will still be attempted.");
                }
            }
            catch (Exception e)
            {
                Log.LogError("Failed to install Restful Tweaks patch: " + e);
            }
        }

        private void Update()
        {
            if (Time.unscaledTime < nextUiScan)
                return;

            nextUiScan = Time.unscaledTime + 0.5f;

            try
            {
                EnsureShopButton();
            }
            catch (Exception e)
            {
                Log.LogWarning("Shop UI scan failed: " + Unwrap(e).Message);
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
                catch { }
            }
            return null;
        }

        private static bool Prefix()
        {
            try { Refresh(); }
            catch (Exception e) { Log.LogError("Shop reroll failed: " + Unwrap(e)); }
            return false;
        }

        private static void EnsureShopButton()
        {
            if (RerollButton != null && RerollButton.activeInHierarchy)
                return;

            if (RerollButton != null)
            {
                try { Destroy(RerollButton); } catch { }
                RerollButton = null;
            }

            MonoBehaviour[] all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            MonoBehaviour best = null;
            int bestScore = 0;

            foreach (MonoBehaviour mb in all)
            {
                if (mb == null || mb.gameObject == null || !mb.gameObject.activeInHierarchy)
                    continue;

                string typeName = mb.GetType().Name ?? "";
                string goName = mb.gameObject.name ?? "";
                int score = ShopUiScore(typeName, goName);
                if (score <= 0)
                    continue;

                string key = typeName + " @ " + goName;
                if (LoggedShopCandidates.Add(key))
                    Log.LogInfo("Active shop UI candidate: " + key + " (score=" + score + ")");

                if (score > bestScore && FindButtonComponentInChildren(mb.gameObject) != null)
                {
                    best = mb;
                    bestScore = score;
                }
            }

            if (best == null)
                return;

            Component sourceButton = FindPreferredButton(best.gameObject);
            if (sourceButton == null)
                return;

            GameObject clone = Instantiate(sourceButton.gameObject, sourceButton.transform.parent);
            clone.name = "ShopRerollButton";

            RectTransform srcRect = sourceButton.GetComponent<RectTransform>();
            RectTransform cloneRect = clone.GetComponent<RectTransform>();
            if (srcRect != null && cloneRect != null)
                cloneRect.anchoredPosition = srcRect.anchoredPosition + new Vector2(0f, -(srcRect.rect.height + 12f));
            else
                clone.transform.localPosition += new Vector3(0f, -60f, 0f);

            Component clonedButton = FindButtonComponent(clone);
            if (clonedButton == null)
            {
                Destroy(clone);
                return;
            }

            RewireClick(clonedButton);
            SetButtonLabel(clone, "Reroll");
            clone.SetActive(true);
            RerollButton = clone;

            Log.LogInfo("Created shop Reroll button under " + best.GetType().Name + "/" + best.gameObject.name +
                        " by cloning " + sourceButton.gameObject.name + ".");
        }

        private static int ShopUiScore(string typeName, string goName)
        {
            string s = (typeName + " " + goName).ToLowerInvariant();
            if (s.Contains("database") || s.Contains("accessor") || s.Contains("itemshop"))
                return 0;

            int score = 0;
            if (s.Contains("shopui")) score += 10;
            if (s.Contains("shop")) score += 6;
            if (s.Contains("store")) score += 4;
            if (s.Contains("merchant")) score += 4;
            if (s.Contains("market")) score += 2;
            if (s.Contains("menu")) score += 2;
            if (s.Contains("content")) score += 1;
            return score;
        }

        private static Component FindPreferredButton(GameObject root)
        {
            Component[] comps = root.GetComponentsInChildren<Component>(true);
            Component first = null;

            foreach (Component c in comps)
            {
                if (!IsButton(c))
                    continue;

                if (c.gameObject.name == "ShopRerollButton")
                    continue;

                if (first == null)
                    first = c;

                string n = (c.gameObject.name ?? "").ToLowerInvariant();
                if (n.Contains("versatile") || n.Contains("buy") || n.Contains("confirm") || n.Contains("close"))
                    return c;
            }

            return first;
        }

        private static Component FindButtonComponentInChildren(GameObject root)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
                if (IsButton(c)) return c;
            return null;
        }

        private static Component FindButtonComponent(GameObject go)
        {
            foreach (Component c in go.GetComponents<Component>())
                if (IsButton(c)) return c;
            return null;
        }

        private static bool IsButton(Component c)
        {
            return c != null && c.GetType().FullName == "UnityEngine.UI.Button";
        }

        private static void RewireClick(Component button)
        {
            PropertyInfo p = button.GetType().GetProperty("onClick", BindingFlags.Public | BindingFlags.Instance);
            if (p == null)
                throw new MissingMemberException("UnityEngine.UI.Button.onClick not found");

            object evt = p.GetValue(button, null);
            if (evt == null)
                throw new MissingMemberException("Button.onClick returned null");

            MethodInfo remove = evt.GetType().GetMethod("RemoveAllListeners", Type.EmptyTypes);
            if (remove != null)
                remove.Invoke(evt, null);

            MethodInfo add = null;
            foreach (MethodInfo m in evt.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "AddListener" && m.GetParameters().Length == 1)
                {
                    add = m;
                    break;
                }
            }

            if (add == null)
                throw new MissingMethodException("ButtonClickedEvent.AddListener not found");

            Type delegateType = add.GetParameters()[0].ParameterType;
            MethodInfo handler = typeof(Plugin).GetMethod("OnRerollClicked", BindingFlags.NonPublic | BindingFlags.Static);
            Delegate callback = Delegate.CreateDelegate(delegateType, handler);
            add.Invoke(evt, new object[] { callback });
        }

        private static void SetButtonLabel(GameObject button, string label)
        {
            foreach (Component c in button.GetComponentsInChildren<Component>(true))
            {
                if (c == null)
                    continue;

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
            }
            catch (Exception e)
            {
                Log.LogError("Shop Reroll button failed: " + Unwrap(e));
            }
        }

        private static void Refresh()
        {
            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null) throw new MissingMemberException("ShopDatabaseAccessor not found");

            object accessor = FindInstance(accessorType);
            MethodInfo getAll = FindMethod(accessorType, "GetAllShops", 0);
            if (getAll == null) throw new MissingMethodException("GetAllShops not found");

            object raw = getAll.Invoke(getAll.IsStatic ? null : accessor, null);
            List<object> shops = Values(raw);
            Log.LogInfo("Shop reroll: GetAllShops returned " + shops.Count + " shop record(s).");

            int limited = 0;
            int refreshed = 0;
            foreach (object shop in shops)
            {
                if (shop == null || !Limited(shop))
                    continue;

                limited++;
                MethodInfo create = FindCreate(accessorType, shop.GetType());
                if (create == null)
                    throw new MissingMethodException("CreateNewShopList not found for shop type " + shop.GetType().FullName);

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

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in a.GetTypes())
                        if (t.Name == name) return t;
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
            if (field != null && field.FieldType == typeof(bool))
                return (bool)field.GetValue(shop);

            PropertyInfo prop = t.GetProperty("limitedItems", f);
            if (prop != null && prop.PropertyType == typeof(bool))
                return (bool)prop.GetValue(shop, null);

            foreach (FieldInfo candidate in t.GetFields(f))
            {
                if (candidate.FieldType == typeof(bool) &&
                    candidate.Name.IndexOf("limited", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (bool)candidate.GetValue(shop);
            }

            foreach (PropertyInfo candidate in t.GetProperties(f))
            {
                if (candidate.PropertyType == typeof(bool) &&
                    candidate.Name.IndexOf("limited", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (bool)candidate.GetValue(shop, null);
            }

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
