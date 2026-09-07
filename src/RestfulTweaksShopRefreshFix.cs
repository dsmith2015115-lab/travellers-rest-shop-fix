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
    [BepInPlugin("dsmith.travellersrest.restfultweaks.shoprefreshfix", "Restful Tweaks Shop Refresh Fix", "1.6.0")]
    [BepInDependency("net.nep.bepinex.restfultweaks", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string HarmonyId = "dsmith.travellersrest.restfultweaks.shoprefreshfix";
        private static readonly string[] ShopUiTypeNames = { "ShopBaseUI", "ShopUI", "FerroShopUI", "AnimalShopUI" };
        private static readonly Dictionary<int, GameObject> Buttons = new Dictionary<int, GameObject>();
        private static readonly HashSet<MethodBase> PatchedUiUpdates = new HashSet<MethodBase>();
        private static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            try
            {
                Harmony harmony = new Harmony(HarmonyId);

                MethodBase oldRefresh = FindShopRefresh();
                if (oldRefresh != null)
                {
                    harmony.Patch(oldRefresh,
                        prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(RestfulTweaksRefreshPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    Log.LogInfo("v1.6.0 patched Restful Tweaks " + oldRefresh.DeclaringType.FullName + "." + oldRefresh.Name + ".");
                }

                InstallShopUiUpdateHooks(harmony);
                Log.LogInfo("v1.6.0 safe all-shop mode active. No vendor OpenShopUI hooks are used.");
            }
            catch (Exception e)
            {
                Log.LogError("Startup failed: " + e);
            }
        }

        private static void InstallShopUiUpdateHooks(Harmony harmony)
        {
            int patched = 0;
            MethodInfo postfix = typeof(Plugin).GetMethod(nameof(ShopUiUpdatePostfix), BindingFlags.NonPublic | BindingFlags.Static);

            foreach (string typeName in ShopUiTypeNames)
            {
                Type t = FindType(typeName);
                if (t == null)
                {
                    Log.LogWarning("Shop UI type not found: " + typeName);
                    continue;
                }

                MethodInfo update = AccessTools.Method(t, "Update", Type.EmptyTypes);
                if (update == null)
                {
                    Log.LogInfo("No Update() method found on " + t.FullName + ".");
                    continue;
                }

                if (!PatchedUiUpdates.Add(update))
                    continue;

                harmony.Patch(update, postfix: new HarmonyMethod(postfix));
                patched++;
                Log.LogInfo("Hooked shop UI update: " + update.DeclaringType.FullName + ".Update().");
            }

            Log.LogInfo("All-shop UI hooks installed=" + patched + ".");
        }

        private static void ShopUiUpdatePostfix(object __instance)
        {
            try
            {
                Component ui = __instance as Component;
                if (ui == null || ui.gameObject == null || !ui.gameObject.activeInHierarchy)
                    return;

                EnsureButton(ui);
            }
            catch (Exception e)
            {
                Log.LogWarning("Shop button injection failed: " + Unwrap(e).Message);
            }
        }

        private static void EnsureButton(Component shopUi)
        {
            int id = shopUi.GetInstanceID();
            GameObject existing;
            if (Buttons.TryGetValue(id, out existing) && existing != null)
                return;

            GameObject alreadyThere = FindNamedChild(shopUi.gameObject, "ShopRerollButton");
            if (alreadyThere != null)
            {
                Buttons[id] = alreadyThere;
                return;
            }

            Component wrapper = FindVersatileButton(shopUi.gameObject);
            GameObject sourceRoot = wrapper != null ? wrapper.gameObject : null;
            Component sourceButton = sourceRoot != null ? FindUnityButton(sourceRoot) : null;

            if (sourceButton == null)
            {
                sourceButton = FindUnityButton(shopUi.gameObject);
                sourceRoot = sourceButton != null ? sourceButton.gameObject : null;
            }

            if (sourceRoot == null || sourceButton == null)
                return;

            Transform parent = sourceRoot.transform.parent;
            if (parent == null)
                return;

            GameObject clone = UnityEngine.Object.Instantiate(sourceRoot, parent);
            clone.name = "ShopRerollButton";

            PositionClone(sourceRoot, clone);
            SetButtonLabel(clone, "Reroll");

            Component clonedButton = FindUnityButton(clone);
            if (clonedButton == null)
            {
                UnityEngine.Object.Destroy(clone);
                return;
            }

            RerollClickProxy proxy = clone.AddComponent<RerollClickProxy>();
            proxy.ShopUi = shopUi;
            RewireClick(clonedButton, proxy);

            clone.SetActive(true);
            Buttons[id] = clone;
            Log.LogInfo("Created Reroll button for " + shopUi.GetType().Name + " at " + HierarchyPath(clone.transform) + ".");
        }

        private static void PositionClone(GameObject source, GameObject clone)
        {
            Transform parent = source.transform.parent;
            bool hasLayout = false;
            foreach (Component c in parent.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n.IndexOf("LayoutGroup", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasLayout = true;
                    break;
                }
            }

            if (hasLayout)
            {
                clone.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
                return;
            }

            RectTransform src = source.GetComponent<RectTransform>();
            RectTransform dst = clone.GetComponent<RectTransform>();
            if (src != null && dst != null)
            {
                float w = src.rect.width;
                if (w < 1f) w = 120f;
                dst.anchoredPosition = src.anchoredPosition + new Vector2(w + 12f, 0f);
            }
            else
            {
                clone.transform.localPosition += new Vector3(140f, 0f, 0f);
            }
        }

        internal static void OnRerollClicked(Component shopUi)
        {
            try
            {
                Log.LogInfo("Reroll clicked in " + (shopUi != null ? shopUi.GetType().Name : "unknown shop UI") + ".");

                bool single = RefreshCurrentShop(shopUi);
                if (!single)
                {
                    Log.LogWarning("Could not map this UI to one shop record; falling back to refreshing all limited shops.");
                    RefreshAllLimitedShops();
                }

                TrySafeVisibleRefresh(shopUi);
            }
            catch (Exception e)
            {
                Log.LogError("Shop Reroll click failed: " + Unwrap(e));
            }
        }

        private static bool RestfulTweaksRefreshPrefix()
        {
            try { RefreshAllLimitedShops(); }
            catch (Exception e) { Log.LogError("Restful Tweaks ShopRefresh redirect failed: " + Unwrap(e)); }
            return false;
        }

        private static bool RefreshCurrentShop(Component shopUi)
        {
            if (shopUi == null)
                return false;

            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null)
                return false;

            object accessor = FindInstance(accessorType);
            MethodInfo getAll = FindMethod(accessorType, "GetAllShops", 0);
            if (getAll == null)
                return false;

            List<object> shops = Values(getAll.Invoke(getAll.IsStatic ? null : accessor, null));
            object current = FindShopRecordReferencedByUi(shopUi, shops);
            if (current == null)
                return false;

            MethodInfo create = FindCreate(accessorType, current.GetType());
            if (create == null)
                return false;

            InvokeCreate(create, accessor, current);
            Log.LogInfo("Rerolled current shop record: " + DescribeShop(current) + ".");
            return true;
        }

        private static object FindShopRecordReferencedByUi(Component ui, List<object> shops)
        {
            if (shops == null || shops.Count == 0)
                return null;

            HashSet<object> set = new HashSet<object>(shops, ReferenceEqualityComparer.Instance);
            Type t = ui.GetType();

            while (t != null && t != typeof(MonoBehaviour) && t != typeof(Component))
            {
                BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

                foreach (FieldInfo field in t.GetFields(f))
                {
                    try
                    {
                        object value = field.GetValue(ui);
                        if (value != null && set.Contains(value))
                            return value;
                    }
                    catch { }
                }

                foreach (PropertyInfo prop in t.GetProperties(f))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length != 0) continue;
                    try
                    {
                        object value = prop.GetValue(ui, null);
                        if (value != null && set.Contains(value))
                            return value;
                    }
                    catch { }
                }

                t = t.BaseType;
            }

            return null;
        }

        private static void RefreshAllLimitedShops()
        {
            Type accessorType = FindType("ShopDatabaseAccessor");
            if (accessorType == null)
                throw new MissingMemberException("ShopDatabaseAccessor not found");

            object accessor = FindInstance(accessorType);
            MethodInfo getAll = FindMethod(accessorType, "GetAllShops", 0);
            if (getAll == null)
                throw new MissingMethodException("ShopDatabaseAccessor.GetAllShops() not found");

            List<object> shops = Values(getAll.Invoke(getAll.IsStatic ? null : accessor, null));
            int limited = 0;
            int refreshed = 0;

            foreach (object shop in shops)
            {
                if (shop == null || !Limited(shop))
                    continue;

                limited++;
                MethodInfo create = FindCreate(accessorType, shop.GetType());
                if (create == null)
                    continue;

                InvokeCreate(create, accessor, shop);
                refreshed++;
            }

            Log.LogInfo("Shop reroll complete: refreshed " + refreshed + "/" + limited + " limited shop(s).");
        }

        private static void InvokeCreate(MethodInfo create, object accessor, object shop)
        {
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
        }

        private static void TrySafeVisibleRefresh(Component ui)
        {
            if (ui == null)
                return;

            string[] methodNames = { "Refresh", "RefreshUI", "UpdateUI", "UpdateShop" };
            Type t = ui.GetType();

            foreach (string name in methodNames)
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (m == null) continue;

                try
                {
                    m.Invoke(ui, null);
                    Log.LogInfo("Refreshed visible shop UI via " + t.Name + "." + name + "().");
                    return;
                }
                catch (Exception e)
                {
                    Log.LogWarning("Visible shop refresh " + t.Name + "." + name + " failed: " + Unwrap(e).Message);
                }
            }

            Log.LogInfo("No safe explicit refresh method found on " + t.Name + "; the rerolled stock may appear after closing and reopening the shop.");
        }

        private static Component FindVersatileButton(GameObject root)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.gameObject.name == "ShopRerollButton") continue;
                if (string.Equals(c.GetType().Name, "VersatileButton", StringComparison.Ordinal))
                    return c;
            }
            return null;
        }

        private static Component FindUnityButton(GameObject root)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().FullName == "UnityEngine.UI.Button")
                    return c;
            return null;
        }

        private static GameObject FindNamedChild(GameObject root, string name)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in transforms)
                if (t != null && t.gameObject.name == name)
                    return t.gameObject;
            return null;
        }

        private static void RewireClick(Component button, RerollClickProxy proxy)
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
            MethodInfo handler = typeof(RerollClickProxy).GetMethod(nameof(RerollClickProxy.InvokeReroll), BindingFlags.Public | BindingFlags.Instance);
            Delegate callback = Delegate.CreateDelegate(delegateType, proxy, handler);
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
                if (m.Name == name && m.GetParameters().Length == count)
                    return m;
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

        private static string DescribeShop(object shop)
        {
            if (shop == null) return "null";
            return shop.GetType().FullName ?? shop.GetType().Name;
        }

        private static string HierarchyPath(Transform t)
        {
            if (t == null) return "<null>";
            List<string> names = new List<string>();
            while (t != null)
            {
                names.Add(t.gameObject.name);
                t = t.parent;
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null)
                e = e.InnerException;
            return e;
        }
    }

    public sealed class RerollClickProxy : MonoBehaviour
    {
        public Component ShopUi;

        public void InvokeReroll()
        {
            Plugin.OnRerollClicked(ShopUi);
        }
    }

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
        public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
    }
}
