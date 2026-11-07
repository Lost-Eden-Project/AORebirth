using System.Collections;
using System.Reflection;
static class Shop {
  static string Show(object v) {
    if (v == null) return "null";
    var t = v.GetType();
    if (t.IsPrimitive || v is string || t.IsEnum) return v.ToString();
    if (v is IEnumerable e && !(v is string)) return "[" + string.Join(", ", e.Cast<object>().Select(Show)) + "]";
    return t.Name + "{" + string.Join(" ", t.GetFields(BindingFlags.Public|BindingFlags.Instance).Select(f => f.Name + "=" + Show(f.GetValue(v))).Concat(t.GetProperties().Select(p => p.Name + "=" + Show(p.GetValue(v))))) + "}";
  }
  public static void Run(string aoPath, int[] ids) {
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    var rdb = new AODB.RdbController(aoPath);
    foreach (int id in ids) {
      var item = rdb.Get<AODB.Common.RDBObjects.ItemBase>((AODB.Common.RDBObjects.ResourceTypeId)1000020, id);
      Console.WriteLine($"== {id} {item?.Name} complete={item?.ReadComplete} stop={item?.ReadStopReason} shopInv={(item?.ShopInventory?.Count ?? -1)}");
      if (item?.ShopInventory != null) foreach (var s in item.ShopInventory) Console.WriteLine("  " + Show(s));
    }
  }
}
