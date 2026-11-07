import re
p='Program.cs'; s=open(p,encoding='utf-8').read()
start=s.index('if (args[2] == "scan") {'); end=s.index('var ids = args.Skip(2)')
s=s[:start]+r'''if (args[2] == "scan") {
    var rx = new System.Text.RegularExpressions.Regex(@"Values=\[([A-Z0-9]{4,6}), (-?\d+), (-?\d+)[^\]]*\]\} FunctionType=53064 ");
    foreach (var item in items) {
        string dump = Show(item);
        var name = System.Text.RegularExpressions.Regex.Match(dump, @"Name=(.*?) Rdb=").Groups[1].Value;
        if (!(name.Contains("Boss") || name.Contains("Lootgiver"))) continue;
        var parts = rx.Matches(dump).Select(m => m.Groups[1].Value + "/" + m.Groups[2].Value + "/" + m.Groups[3].Value).ToList();
        if (parts.Count > 0) Console.WriteLine(item.GetType().GetField("ID").GetValue(item) + "\t" + name + "\t" + string.Join(" ", parts));
    }
    return;
}
'''+s[end:]
open(p,'w',encoding='utf-8').write(s)
