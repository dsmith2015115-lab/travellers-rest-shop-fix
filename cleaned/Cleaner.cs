using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Cleaner
{
    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: Cleaner <input.dll> <output.dll>");
            return 2;
        }

        var input = args[0];
        var output = args[1];
        var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { ReadWrite = false, InMemory = true });
        var module = asm.MainModule;

        var plugin = module.Types.FirstOrDefault(t => t.FullName == "RestfulTweaks.Plugin");
        var compat = module.Types.FirstOrDefault(t => t.FullName == "RestfulTweaks.Cleaned.ModernShopCompat");
        if (plugin == null) throw new InvalidOperationException("RestfulTweaks.Plugin not found after merge");
        if (compat == null) throw new InvalidOperationException("ModernShopCompat was not merged into the Redux assembly");

        int harmonyArgsFixed = 0;
        foreach (var type in module.Types.SelectMany(AllTypes))
        foreach (var method in type.Methods)
        foreach (var p in method.Parameters)
        {
            if (p.Name == "PLEKGADBABI")
            {
                p.Name = "__0";
                harmonyArgsFixed++;
            }
        }

        int configKeysFixed = 0;
        var ctor = plugin.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        if (ctor != null && ctor.HasBody)
        {
            int cleanRoomsSeen = 0;
            foreach (var i in ctor.Body.Instructions)
            {
                if (i.OpCode == OpCodes.Ldstr && (string)i.Operand == "Clean Rooms")
                {
                    cleanRoomsSeen++;
                    if (cleanRoomsSeen == 2) { i.Operand = "Never Angry"; configKeysFixed++; }
                    else if (cleanRoomsSeen == 3) { i.Operand = "Can Calm"; configKeysFixed++; }
                }
            }
        }

        var init = compat.Methods.FirstOrDefault(m => m.Name == "Initialize" && m.IsStatic && m.Parameters.Count == 0);
        var awake = plugin.Methods.FirstOrDefault(m => m.Name == "Awake" && !m.IsStatic && m.Parameters.Count == 0);
        if (init == null || awake == null || !awake.HasBody) throw new InvalidOperationException("Unable to find Awake()/ModernShopCompat.Initialize()");

        var importedInit = module.ImportReference(init);
        var il = awake.Body.GetILProcessor();
        var rets = awake.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
        foreach (var ret in rets) il.InsertBefore(ret, il.Create(OpCodes.Call, importedInit));

        var info = module.Types.FirstOrDefault(t => t.FullName == "RestfulTweaks.PluginInfo");
        if (info != null)
        {
            var version = info.Fields.FirstOrDefault(f => f.Name == "PLUGIN_VERSION" && f.HasConstant && f.Constant is string);
            if (version != null) version.Constant = "1.7.1-clean";
        }

        foreach (var ca in plugin.CustomAttributes)
        {
            if (ca.AttributeType.FullName == "BepInEx.BepInPlugin" && ca.ConstructorArguments.Count >= 3)
                ca.ConstructorArguments[2] = new CustomAttributeArgument(module.TypeSystem.String, "1.7.1-clean");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        asm.Write(output);
        asm.Dispose();

        Console.WriteLine($"Cleaned Redux written to {output}");
        Console.WriteLine($"Harmony argument bindings repaired: {harmonyArgsFixed}");
        Console.WriteLine($"Duplicate customer config keys repaired: {configKeysFixed}");
        Console.WriteLine("Modern shop backend injected into original Plugin.Awake().");
        return 0;
    }

    private static System.Collections.Generic.IEnumerable<TypeDefinition> AllTypes(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes)
            foreach (var x in AllTypes(n))
                yield return x;
    }
}
