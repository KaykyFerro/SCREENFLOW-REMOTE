using System.Windows.Controls;

namespace ScreenFlow.App.Pages;

/// <summary>Item de lista com valor e texto (o ComboBox mostra o texto).</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public static class ChoiceExtensions
{
    public static void Fill<T>(this ComboBox combo, IEnumerable<Choice<T>> choices)
    {
        combo.Items.Clear();
        foreach (var choice in choices) combo.Items.Add(choice);
    }

    /// <summary>Seleciona o item com o valor (ou o mais próximo, quando há comparação).</summary>
    public static void SelectValue<T>(this ComboBox combo, T value, Func<T, double>? distance = null)
    {
        var items = combo.Items.OfType<Choice<T>>().ToList();
        var match = items.FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value));
        if (match is null && distance is not null && items.Count > 0) match = items.OrderBy(c => Math.Abs(distance(c.Value) - distance(value))).First();
        combo.SelectedItem = match ?? items.FirstOrDefault();
    }

    public static T? Value<T>(this ComboBox combo) => combo.SelectedItem is Choice<T> choice ? choice.Value : default;
}
