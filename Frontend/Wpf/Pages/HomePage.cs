using System;
using System.Windows;
using System.Windows.Controls;
namespace UtilityTool.Frontend.Wpf
{
    internal sealed class HomePage : Grid
    {
        internal HomePage(Action<string> navigate, bool preview)
        {
            Margin = new Thickness(32, 28, 32, 24);
            var stack = new StackPanel(); Children.Add(stack);
            stack.Children.Add(Ui.Heading("A little more control.", "Your everyday Windows utilities, together in one place."));
            var hero = new StackPanel(); hero.Children.Add(Ui.Text("UtilityTool", 24));
            var description = Ui.Text("Set a shutdown timer, protect selected apps with your own password, or listen through multiple audio outputs.", 15, true); description.Margin = new Thickness(0, 12, 0, 0); hero.Children.Add(description);
            var card = Ui.Card(hero, new Thickness(28)); card.Margin = new Thickness(0, 0, 0, 24); stack.Children.Add(card);
            var tiles = new Grid(); for (int i = 0; i < 3; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition());
            string[] names = { "Shutdown", "Lock", "Sound" }, captions = { "Turn off Windows on your schedule.", "Choose the apps that need a password.", "Play audio on more than one device." };
            for (int i = 0; i < 3; i++)
            { string name = names[i]; var body = new StackPanel(); body.Children.Add(Ui.Text(name, 20)); var label = Ui.Text(captions[i], 14, true); label.Margin = new Thickness(0, 12, 0, 24); body.Children.Add(label); var button = Ui.Button("Open " + name.ToLowerInvariant()); button.Click += delegate { navigate(name); }; body.Children.Add(button); var tile = Ui.Card(body); tile.Margin = new Thickness(i == 0 ? 0 : 8, 0, i == 2 ? 0 : 8, 0); Grid.SetColumn(tile, i); tiles.Children.Add(tile); }
            stack.Children.Add(tiles);
            var footer = Ui.Text(preview ? "PREVIEW • Sample apps and devices. System actions and saved settings are disabled." : "Closing UtilityTool stops audio mirroring. App locks require UtilityTool to stay open.", 13, true); footer.Margin = new Thickness(0, 24, 0, 0); stack.Children.Add(footer);
        }
    }
}
