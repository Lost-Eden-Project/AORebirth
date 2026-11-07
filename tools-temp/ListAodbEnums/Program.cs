using System;
using System.Reflection;

static class Program
{
    static void Main(string[] args)
    {
        string dll = args.Length > 0
            ? args[0]
            : @"C:\Users\tyler\AppData\Local\Temp\cursor-sandbox-cache\0ef8f5acc19ad274fcbda20766272962\nuget\aodb\1.0.7\lib\net10.0\AODB.Common.dll";
        Assembly asm = Assembly.LoadFrom(dll);
        Type enumType = asm.GetType("AODB.Common.RDBObjects.ResourceTypeId");
        if (enumType == null)
        {
            foreach (Type type in asm.GetTypes())
            {
                if (type.Name.Contains("ResourceType", StringComparison.Ordinal))
                    Console.WriteLine(type.FullName);
            }
            return;
        }

        foreach (string name in Enum.GetNames(enumType))
            Console.WriteLine(name + "=" + Convert.ToInt64(Enum.Parse(enumType, name)));

        Type tilemap = asm.GetType("AODB.Common.RDBObjects.Tilemap");
        if (tilemap != null)
        {
            Console.WriteLine("--- Tilemap members ---");
            foreach (MemberInfo member in tilemap.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is PropertyInfo || member is FieldInfo)
                    Console.WriteLine(member.MemberType + " " + member.Name);
            }
        }
    }
}
