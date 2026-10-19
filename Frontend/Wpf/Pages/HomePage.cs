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
            var description = Ui.Text("Save shutdown times, protect selected apps, listen through multiple audio outputs, or play your own soundboard clips.", 15, true); description.Margin = new Thickness(0, 12, 0, 0); hero.Children.Add(description);
            var card = Ui.Card(hero, new Thickness(28)); card.Margin = new Thickness(0, 0, 0, 24); stack.Children.Add(card);
            var tiles = new Grid(); for (int i = 0; i < 4; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition());
            string[] names = { "Shutdown", "Lock", "Sound", "Soundboard" }, captions = { "Turn off Windows on your schedule.", "Choose which apps require your password.", "Play audio on more than one device.", "Your sounds, ready with one click." };
            for (int i = 0; i < names.Length; i++)
            { string name = names[i]; var body = new StackPanel(); body.Children.Add(Ui.Text(name, 20)); var label = Ui.Text(captions[i], 14, true); label.MinHeight = 66; label.Margin = new Thickness(0, 12, 0, 12); body.Children.Add(label); var button = Ui.Button("Open " + name.ToLowerInvariant()); button.Padding = new Thickness(12, 10, 12, 10); button.Click += delegate { navigate(name); }; body.Children.Add(button); var tile = Ui.Card(body, new Thickness(18)); tile.Margin = new Thickness(i == 0 ? 0 : 8, 0, i == names.Length - 1 ? 0 : 8, 0); Grid.SetColumn(tile, i); tiles.Children.Add(tile); }
            stack.Children.Add(tiles);
            var footer = Ui.Text(preview ? "SAMPLE PREVIEW • Apps, sounds and devices are examples; actions are disabled. For working controls, choose Terminal → Run Task → Run UtilityTool." : "Keep UtilityTool open for app protection. Closing it while protection is on requires your password and stops protection and all audio streams.", 13, true); footer.Margin = new Thickness(0, 24, 0, 0); stack.Children.Add(footer);
        }
    }
}
