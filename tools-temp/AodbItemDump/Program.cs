using System.Text;
using AODB.Common.RDBObjects;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
foreach (var name in Enum.GetNames(typeof(SkillCheck)))
    Console.WriteLine($"{name}={(int)Enum.Parse(typeof(SkillCheck), name)}");
