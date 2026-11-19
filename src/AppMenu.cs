using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ChengyuDock;
internal static class AppMenu
{
    public static ContextMenu Create(string title)
    {
        var menu = new ContextMenu(); var heading = new MenuItem { Header = title, IsEnabled = false, Focusable = false, FontSize = 11, Padding = new Thickness(9, 5, 9, 5) }; menu.Items.Add(heading); return menu;
    }
    public static MenuItem Action(ContextMenu menu, string label, Action action, string icon = "", bool? selected = null, string hint = "")
    {
        var item = new MenuItem { Header = label, Icon = Ui.Text(icon, 13, "#AFC4DE"), InputGestureText = hint, IsCheckable = selected.HasValue, IsChecked = selected == true };
        item.Click += (_, _) => action(); menu.Items.Add(item); return item;
    }
    public static void Divider(ContextMenu menu) => menu.Items.Add(new Separator());
    public static void Materials(ContextMenu menu, string current, Action<string> set, bool solid = false)
    {
        foreach (var (label, key) in new[] { ("液态玻璃", "liquid"), ("磨砂玻璃", "frosted"), ("无背景", "none") }) Action(menu, label, () => set(key), "◈", current == key);
        if (solid) Action(menu, "纯黑实体", () => set("solid"), "●", current == "solid");
    }
    public static void Show(ContextMenu menu, FrameworkElement target)
    { menu.PlacementTarget = target; menu.Placement = PlacementMode.MousePoint; menu.IsOpen = true; }
    public static void Manager(string page) { App.Current.ShowManager(); App.Current.Manager.Navigate(page); }
}
