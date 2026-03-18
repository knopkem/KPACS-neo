// Quick script to inspect OpenCL.Net.dll API surface
using System;
using System.Linq;
using System.Reflection;

var dllPath = @"D:\GitHub\delphi\Projects\IQView\CSharp\KPACS.Viewer.Avalonia\bin\Debug\net10.0\OpenCL.Net.dll";
var asm = Assembly.LoadFrom(dllPath);

Console.WriteLine("=== ALL EXPORTED TYPES ===");
foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
{
    string kind = t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsInterface ? "interface" : "class";
    Console.WriteLine($"  [{kind}] {t.FullName}");
}

Console.WriteLine();
Console.WriteLine("=== ContextProperties / ContextProperty types ===");
foreach (var t in asm.GetExportedTypes().Where(t => t.Name.Contains("Context", StringComparison.OrdinalIgnoreCase)))
{
    string kind = t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsInterface ? "interface" : "class";
    Console.WriteLine($"\n  [{kind}] {t.FullName}");
    if (t.IsEnum)
    {
        foreach (var val in Enum.GetValues(t))
            Console.WriteLine($"    {val} = {(int)(object)val}");
    }
    else
    {
        foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            Console.WriteLine($"    {m.MemberType}: {m}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Cl.CreateContext overloads ===");
var clType = asm.GetExportedTypes().FirstOrDefault(t => t.Name == "Cl");
if (clType != null)
{
    foreach (var m in clType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "CreateContext"))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  {m.ReturnType.Name} CreateContext({parms})");
    }
}

Console.WriteLine();
Console.WriteLine("=== All Cl static methods (first 50) ===");
if (clType != null)
{
    foreach (var m in clType.GetMethods(BindingFlags.Public | BindingFlags.Static).Take(50))
    {
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}(...)");
    }
}
