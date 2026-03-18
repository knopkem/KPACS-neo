using System;
using System.Linq;
using System.Reflection;

var dllPath = @"D:\GitHub\delphi\Projects\IQView\CSharp\KPACS.Viewer.Avalonia\bin\Debug\net10.0\OpenCL.Net.dll";

// Use MetadataLoadContext to avoid resolving dependencies
var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
var resolver = new PathAssemblyResolver(
    Directory.GetFiles(runtimeDir, "*.dll").Append(dllPath));
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(dllPath);
var types = asm.GetTypes().Where(t => t.IsPublic).OrderBy(t => t.FullName).ToArray();

Console.WriteLine("=== ALL EXPORTED TYPES ===");
foreach (var t in types)
{
    string kind = t.IsEnum ? "enum" : t.IsValueType && !t.IsEnum ? "struct" : t.IsInterface ? "interface" : "class";
    Console.WriteLine($"  [{kind}] {t.FullName}");
}

Console.WriteLine();
Console.WriteLine("=== Context-related types ===");
foreach (var t in types.Where(t => t.Name.Contains("Context", StringComparison.OrdinalIgnoreCase)))
{
    string kind = t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsInterface ? "interface" : "class";
    Console.WriteLine($"\n  [{kind}] {t.FullName}");
    if (t.IsEnum)
    {
        foreach (var name in Enum.GetNames(t))
        {
            var val = Convert.ToInt64(Enum.Parse(t, name));
            Console.WriteLine($"    {name} = 0x{val:X}");
        }
    }
    else
    {
        foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            Console.WriteLine($"    {m.MemberType}: {m}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Cl.CreateContext overloads (detailed) ===");
var clType = types.FirstOrDefault(t => t.Name == "Cl");
if (clType != null)
{
    foreach (var m in clType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "CreateContext"))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p =>
        {
            var pt = p.ParameterType;
            string typeName = pt.IsArray ? pt.GetElementType()!.FullName + "[]" : pt.FullName ?? pt.Name;
            string byRef = p.IsOut ? "out " : pt.IsByRef ? "ref " : "";
            return $"{byRef}{typeName} {p.Name}";
        }));
        Console.WriteLine($"  {m.ReturnType.FullName ?? m.ReturnType.Name} CreateContext({parms})");
    }
}

Console.WriteLine();
Console.WriteLine("=== Property-related types (enums with 'Propert' in name) ===");
foreach (var t in types.Where(t => t.IsEnum && t.Name.Contains("Propert", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine($"\n  [enum] {t.FullName}");
    foreach (var name in Enum.GetNames(t))
    {
        var val = Convert.ToInt64(Enum.Parse(t, name));
        Console.WriteLine($"    {name} = 0x{val:X}");
    }
}
