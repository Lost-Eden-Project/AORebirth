using System.Collections;
using System.Reflection;
static class Nano {
  static string Show(object v, int depth = 0) {
    if (v == null) return "null";
    var t = v.GetType();
    if (t.IsPrimitive || v is string || t.IsEnum) return v.ToString();
    if (depth > 4) return t.Name;
    if (v is IDictionary d) return "{" + string.Join(", ", d.Keys.Cast<object>().Select(k => Show(k) + ": " + Show(d[k], depth + 1))) + "}";
    if (v is IEnumerable e) return "[" + string.Join(", ", e.Cast<object>().Select(x => Show(x, depth + 1))) + "]";
    return t.Name + "{" + string.Join(" ", t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name + "=" + Show(f.GetValue(v), depth + 1))
      .Concat(t.GetProperties().Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name + "=" + Show(p.GetValue(v), depth + 1)))) + "}";
  }
  public static void Run(string aoPath, int type, int id) {
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    var rdb = new AODB.RdbController(aoPath);
    var item = rdb.Get<AODB.Common.RDBObjects.ItemBase>((AODB.Common.RDBObjects.ResourceTypeId)type, id);
    Console.WriteLine($"{item?.Name}");
    foreach (var pr in item.GetType().GetProperties().Concat(item.GetType().GetFields().Select(f=>(MemberInfo)f))) { if (pr.Name.Contains("Attack")||pr.Name.Contains("Defend")||pr.Name.Contains("Skill")) Console.WriteLine(pr.Name + " = " + Show(pr is PropertyInfo pi ? pi.GetValue(item) : ((FieldInfo)pr).GetValue(item))); }
    if (item?.Events != null) foreach (DictionaryEntry ev in (IDictionary)item.Events) Console.WriteLine("EVENT " + ev.Key + " " + Show(ev.Value));
  }
}
