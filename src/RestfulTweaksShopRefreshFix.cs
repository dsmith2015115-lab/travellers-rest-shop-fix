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
    [BepInPlugin("dsmith.travellersrest.restfultweaks.shoprefreshfix", "Restful Tweaks Shop Refresh Fix", "1.4.0")]
    [BepInDependency("net.nep.bepinex.restfultweaks", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string HarmonyId = "dsmith.travellersrest.restfultweaks.shoprefreshfix";
        private static ManualLogSource Log;
        private static Component ActiveShopUi;
        private static GameObject RerollButton;
        private static int pendingFrames;
        private static object pendingContext;
        private static readonly string[] ShopUiTypeNames = { "ShopUI", "FerroShopUI", "AnimalShopUI", "ShopBaseUI" };

        private void Awake()
        {
            Log = Logger;
            Harmony harmony = new Harmony(HarmonyId);

            try
            {
                MethodBase oldRefresh = FindRestfulTweaksShopRefresh();
                if (oldRefresh != null)
                {
                    harmony.Patch(oldRefresh,
                        prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(RestfulTweaksRefreshPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    Log.LogInfo("v1.4.0 patched Restful Tweaks " + oldRefresh.DeclaringType.FullName + "." + oldRefresh.Name + ".");
                }

                int lifecycleHooks = PatchShopOpenLifecycle(harmony);
                Log.LogInfo("v1.4.0 shop lifecycle hooks installed=" + lifecycleHooks + ". F8 is enabled as a direct reroll diagnostic.");

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
            try
            {
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    Log.LogInfo("F8 direct shop reroll requested.");
                    DoRerollAndRefreshVisibleUi();
                }

                if (pendingFrames > 0)
                {
                    pendingFrames--;
                    if (pendingFrames == 0)
                    {
                        Component ui = ResolveShopUiFromContext(pendingContext);
                        pendingContext = null;
                        if (ui != null)
                            TryInjectRerollButton(ui);
                        else
                            Log.LogWarning("Shop opened, but no live ShopUI/ShopBaseUI/FerroShopUI/AnimalShopUI instance could be resolved after the open hook.");
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Update diagnostic failed: " + Unwrap(e));
            }
        }

        private static int PatchShopOpenLifecycle(Harmony harmony)
        {
            int count = 0;
            MethodInfo postfix = typeof(Plugin).GetMethod(nameof(ShopOpenPostfix), BindingFlags.NonPublic | BindingFlags.Static);

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (Type t in types)
                {
                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                    catch { continue; }

                    foreach (MethodInfo m in methods)
                    {
                        if (m.Name != "OpenShopUI")
                            continue;

                        try
                        {
                            harmony.Patch(m, postfix: new HarmonyMethod(postfix));
                            count++;
                            Log.LogInfo("Hooked shop open lifecycle: " + t.FullName + ".OpenShopUI(" + m.GetParameters().Length + " args).");
                        }
                        catch (Exception e)
                        {
                            Log.LogWarning("Could not hook " + t.FullName + ".OpenShopUI: " + e.Message);
                        }
                    }
                }
            }

            return count;
        }

        private static void ShopOpenPostfix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                Log.LogInfo("OpenShopUI fired: " + __originalMethod.DeclaringType.FullName + "." + __originalMethod.Name + ".");

                object context = __instance;
                if (__args != null)
                {
                    foreach (object arg in __args)
                    {
                        if (arg is Component || arg is GameObject)
                        {
                            context = arg;
                            break;
                        }
                    }
                }

                pendingContext = context;
                pendingFrames = 2;
            }
            catch (Exception e)
            {
                Log.LogWarning("OpenShopUI postfix failed: " + Unwrap(e));
            }
        }

        private static Component ResolveShopUiFromContext(object context)
        {
            Component direct = context as Component;
            if (direct != null)
            {
                if (IsShopUiType(direct.GetType()))
                {
                    Log.LogInfo("Resolved live shop UI directly from OpenShopUI instance: " + direct.GetType().Name + ".");
                    return direct;
                }

                Component nearby = FindShopUiInHierarchy(direct.gameObject);
                if (nearby != null)
                {
                    Log.LogInfo("Resolved live shop UI near OpenShopUI owner: " + nearby.GetType().Name + " @ " + HierarchyPath(nearby.transform) + ".");
                    return nearby;
                }
            }

            GameObject go = context as GameObject;
            if (go != null)
            {
                Component nearby = FindShopUiInHierarchy(go);
                if (nearby != null)
                    return nearby;
            }

            // The open hook is the trigger; after it fires we can safely search all loaded instances,
            // including inactive children, without polling continuously.
            Component best = null;
            foreach (string typeName in ShopUiTypeNames)
            {
                Type t = FindType(typeName);
                if (t == null || !typeof(Component).IsAssignableFrom(t))
                    continue;

                UnityEngine.Object[] objects;
                try { objects = Resources.FindObjectsOfTypeAll(t); }
                catch { continue; }

                Log.LogInfo("Post-open lookup: " + typeName + " instances=" + objects.Length + ".");

                foreach (UnityEngine.Object obj in objects)
                {
                    Component c = obj as Component;
                    if (c == null || c.gameObject == null)
                        continue;

                    Log.LogInfo("Post-open candidate: " + typeName + " active=" + c.gameObject.activeInHierarchy +
                                " path=" + HierarchyPath(c.transform) + ".");

                    if (c.gameObject.activeInHierarchy)
                        return c;
                    if (best == null)
                        best = c;
                }
            }

            return best;
        }

        private static Component FindShopUiInHierarchy(GameObject start)
        {
            if (start == null) return null;

            Transform root = start.transform.root;
            Component[] comps = root.GetComponentsInChildren<Component>(true);
            foreach (Component c in comps)
                if (c != null && IsShopUiType(c.GetType()))
                    return c;

            return null;
        }

        private static bool IsShopUiType(Type t)
        {
            if (t == null) return false;
            foreach (string name in ShopUiTypeNames)
                if (t.Name == name) return true;
            return false;
        }

        private static void TryInjectRerollButton(Component ui)
        {
            if (ui == null || ui.gameObject == null)
                return;

            ActiveShopUi = ui;

            if (RerollButton != null)
            {
                try { Destroy(RerollButton); } catch { }
                RerollButton = null;
            }

            Component wrapper = FindVersatileButton(ui.gameObject);
            Component unityButton = wrapper != null ? FindUnityButton(wrapper.gameObject) : FindUnityButton(ui.gameObject);
            GameObject template = wrapper != null ? wrapper.gameObject : (unityButton != null ? unityButton.gameObject : null);

            if (template == null || unityButton == null)
            {
                Log.LogWarning("Live " + ui.GetType().Name + " found, but it has no VersatileButton/UnityEngine.UI.Button to clone. Dumping controls.");
                DumpControls(ui.gameObject);
                return;
            }

            GameObject clone = Instantiate(template, template.transform.parent);
            clone.name = "ShopRerollButton";

            RectTransform srcRect = template.GetComponent<RectTransform>();
            RectTransform dstRect = clone.GetComponent<RectTransform>();
            if (srcRect != null && dstRect != null)
            {
                float h = srcRect.rect.height;
                if (h < 1f) h = 42f;
                dstRect.anchoredPosition = srcRect.anchoredPosition + new Vector2(0f, -(h + 8f));
            }
            else
            {
                clone.transform.localPosition += new Vector3(0f, -55f, 0f);
            }

            Component clonedButton = FindUnityButton(clone);
            if (clonedButton == null)
            {
                Destroy(clone);
                Log.LogError("Cloned template contained no UnityEngine.UI.Button.");
                return;
            }

            RewireClick(clonedButton);
            SetButtonLabel(clone, "Reroll");
            clone.SetActive(true);
            RerollButton = clone;

            Log.LogInfo("Created Reroll button in live " + ui.GetType().Name + " at " + HierarchyPath(clone.transform) + ".");
        }

        private static void DumpControls(GameObject root)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Toggle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log.LogInfo("Shop control candidate: " + n + " @ " + HierarchyPath(c.transform) + ".");
            }
        }

        private static Component FindVersatileButton(GameObject root)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.gameObject.name != "ShopRerollButton" && c.GetType().Name == "VersatileButton")
                    return c;
            return null;
        }

        private static Component FindUnityButton(GameObject root)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().FullName == "UnityEngine.UI.Button")
                    return c;
            return null;
        }

        private static void RewireClick(Component button)
        {
            PropertyInfo p = button.GetType().GetProperty("onClick", BindingFlags.Public | BindingFlags.Instance);
            if (p == null) throw new MissingMemberException("UnityEngine.UI.Button.onClick not found");
            object evt = p.GetValue(button, null);
            if (evt == null) throw new MissingMemberException("Button.onClick returned null");

            MethodInfo remove = evt.GetType().GetMethod("RemoveAllListeners", Type.EmptyTypes);
            if (remove != null) remove.Invoke(evt, null);

            MethodInfo add = null;
            foreach (MethodInfo m in evt.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "AddListener" && m.GetParameters().Length == 1) { add = m; break; }
            if (add == null) throw new MissingMethodException("ButtonClickedEvent.AddListener not found");

            Type delegateType = add.GetParameters()[0].ParameterType;
            MethodInfo handler = typeof(Plugin).GetMethod(nameof(OnRerollClicked), BindingFlags.NonPublic | BindingFlags.Static);
            Delegate callback = Delegate.CreateDelegate(delegateType, handler);
            add.Invoke(evt, new object[] { callback });
        }

        private static void SetButtonLabel(GameObject root, string label)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
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
            DoRerollAndRefreshVisibleUi();
        }

        private static void DoRerollAndRefreshVisibleUi()
        {
            try
            {
                RefreshShopDatabase();
                RefreshVisibleShopUi();
            }
            catch (Exception e)
            {
                Log.LogError("Direct shop reroll failed: " + Unwrap(e));
            }
        }

        private static bool RestfulTweaksRefreshPrefix()
        {
            DoRerollAndRefreshVisibleUi();
            return false;
        }

        private static void RefreshShopDatabase()
        {
            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null) throw new MissingMemberException("ShopDatabaseAccessor not found");

            object accessor = FindSingleton(accessorType);
            MethodInfo getAll = FindMethod(accessorType, "GetAllShops", 0);
            if (getAll == null) throw new MissingMethodException("GetAllShops not found");

            List<object> shops = Values(getAll.Invoke(getAll.IsStatic ? null : accessor, null));
            Log.LogInfo("Reroll database: GetAllShops count=" + shops.Count + ".");

            int limited = 0;
            int refreshed = 0;
            foreach (object shop in shops)
            {
                if (shop == null || !Limited(shop)) continue;
                limited++;

                MethodInfo create = FindCreate(accessorType, shop.GetType());
                if (create == null)
                    throw new MissingMethodException("CreateNewShopList not found for " + shop.GetType().FullName);

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

                Log.LogInfo("Calling CreateNewShopList for " + shop.GetType().Name + " via " + create + ".");
                create.Invoke(create.IsStatic ? null : accessor, args);
                refreshed++;
            }

            Log.LogInfo("Reroll database complete: limited=" + limited + ", refreshed=" + refreshed + ".");
        }

        private static void RefreshVisibleShopUi()
        {
            if (ActiveShopUi == null)
            {
                Log.LogInfo("No captured live shop UI; database reroll completed without visual refresh.");
                return;
            }

            Type t = ActiveShopUi.GetType();
            string[] names = { "OpenShopUI", "Refresh", "RefreshUI", "UpdateShop", "UpdateUI" };
            foreach (string name in names)
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (m == null) continue;

                try
                {
                    m.Invoke(ActiveShopUi, null);
                    Log.LogInfo("Visible shop refreshed through " + t.Name + "." + m.Name + "().");
                    return;
                }
                catch (Exception e)
                {
                    Log.LogWarning("Visible refresh through " + m.Name + " failed: " + Unwrap(e).Message);
                }
            }

            Log.LogInfo("No known zero-argument refresh method on live " + t.Name + ". Close/reopen the shop to inspect database reroll result.");
        }

        private static MethodBase FindRestfulTweaksShopRefresh()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = a.GetName().Name ?? "";
                if (an.IndexOf("RestfulTweaks", StringComparison.OrdinalIgnoreCase) < 0 &&
                    an.IndexOf("RestFulTweaks", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

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

        private static object FindSingleton(Type t)
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

            foreach (PropertyInfo p in t.GetProperties(f))
                if (p.PropertyType == typeof(bool) && p.Name.IndexOf("limited", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (bool)p.GetValue(shop, null);

            return false;
        }

        private static List<object> Values(object source)
        {
            List<object> result = new List<object>();
            if (source == null) return result;

            IDictionary dict = source as IDictionary;
            if (dict != null)
            {
                foreach (object v in dict.Values) if (v != null) result.Add(v);
                return result;
            }

            IEnumerable enumerable = source as IEnumerable;
            if (enumerable != null)
            {
                foreach (object x in enumerable)
                {
                    if (x == null) continue;
                    PropertyInfo value = x.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (value != null)
                    {
                        try
                        {
                            object v = value.GetValue(x, null);
                            if (v != null) result.Add(v);
                            continue;
                        }
                        catch { }
                    }
                    result.Add(x);
                }
            }

            return result;
        }

        private static string HierarchyPath(Transform t)
        {
            if (t == null) return "<null>";
            string path = t.name;
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 32)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null)
                e = e.InnerException;
            return e;
        }
    }
}
