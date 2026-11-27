using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Media;

namespace ScreenFlow.App.Controls;

/// <summary>
/// (1.5.0) O mixer do ScreenFlow: três volumes independentes (a voz da sala, as transmissões e os sons do app), cada um
/// com o controle deslizante de 0 a 100%, o número (dá para digitar) e o mudo. Muda o som na hora e fica guardado sozinho
/// (sem precisar de Salvar). Usado nas Configurações e no painel rápido da voz; todos os mixers abertos andam juntos.
/// </summary>
public sealed class MixerPanel : StackPanel
{
    private readonly List<Row> _rows = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _updating;

    public MixerPanel() : this(compact: false) { }

    public MixerPanel(bool compact)
    {
        _rows.Add(new Row(this, AudioBusKind.Voip, "", "Voz (VOIP)", "Quem fala nos canais de voz da sala.", compact));
        _rows.Add(new Row(this, AudioBusKind.Streams, "", "Transmissões", "O som das telas, câmeras e festas de música que você assiste.", compact));
        _rows.Add(new Row(this, AudioBusKind.AppSounds, "", "Sons do aplicativo", "Avisos, mensagens de voz e figurinhas com som.", compact));
        foreach (var row in _rows) Children.Add(row.Root);
        _saveTimer.Tick += (_, _) => SaveNow();
        Loaded += (_, _) =>
        {
            Refresh();
            foreach (var row in _rows) AudioMixer.Bus(row.Kind).Changed += OnBusChanged;
        };
        Unloaded += (_, _) =>
        {
            foreach (var row in _rows) AudioMixer.Bus(row.Kind).Changed -= OnBusChanged;
            if (_saveTimer.IsEnabled) SaveNow();
        };
    }

    private void OnBusChanged(AudioBus bus)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnBusChanged(bus));
            return;
        }
        if (!_updating) Refresh();
    }

    /// <summary>Mostra o que está valendo agora (outro mixer aberto pode ter mudado).</summary>
    public void Refresh()
    {
        _updating = true;
        try { foreach (var row in _rows) row.Show(AudioMixer.Bus(row.Kind)); }
        finally { _updating = false; }
    }

    private void Changed(Row row, int percent, bool muted)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var mixer = AppServices.Settings.Current.Mixer;
            switch (row.Kind)
            {
                case AudioBusKind.Voip: mixer.VoipPercent = percent; mixer.VoipMuted = muted; break;
                case AudioBusKind.Streams: mixer.StreamsPercent = percent; mixer.StreamsMuted = muted; break;
                default: mixer.AppSoundsPercent = percent; mixer.AppSoundsMuted = muted; break;
            }
            // Vale na hora (o som muda em rampa, sem estalo); o arquivo é gravado logo depois de parar de mexer.
            AudioMixer.Bus(row.Kind).Set(percent, muted);
            row.Show(AudioMixer.Bus(row.Kind));
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        finally { _updating = false; }
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        try { AppServices.Settings.Save(); }
        catch (Exception exception) { Core.Diagnostics.Log.Warn($"Não deu para guardar o mixer: {exception.Message}"); }
    }

    private sealed class Row
    {
        private readonly MixerPanel _owner;
        private readonly Slider _slider;
        private readonly TextBox _number;
        private readonly ToggleButton _mute;
        private readonly string _name;

        public Row(MixerPanel owner, AudioBusKind kind, string icon, string name, string hint, bool compact)
        {
            _owner = owner;
            _name = name;
            Kind = kind;
            _slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1, MinWidth = compact ? 150 : 120 };
            var grid = new Grid { Margin = new Thickness(0, 0, 0, compact ? 10 : 16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _mute = new ToggleButton
            {
                Content = icon,
                Width = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            _mute.SetResourceReference(StyleProperty, "VoiceToggle");
            AutomationProperties.SetName(_mute, $"Silenciar: {name}");
            _mute.Click += (_, _) => _owner.Changed(this, (int)Math.Round(_slider.Value), _mute.IsChecked == true);
            grid.Children.Add(_mute);

            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = name, Margin = new Thickness(0, 0, 0, 2) };
            title.SetResourceReference(StyleProperty, "Label");
            middle.Children.Add(title);
            if (!compact)
            {
                var caption = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                caption.SetResourceReference(StyleProperty, "Caption");
                middle.Children.Add(caption);
            }
            AutomationProperties.SetName(_slider, $"Volume: {name}");
            _slider.ValueChanged += (_, e) =>
            {
                if (_owner._updating) return;
                _owner.Changed(this, (int)Math.Round(e.NewValue), _mute.IsChecked == true);
            };
            middle.Children.Add(_slider);
            Grid.SetColumn(middle, 1);
            grid.Children.Add(middle);

            var numberPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            _number = new TextBox { Width = 52, TextAlignment = TextAlignment.Right, MaxLength = 3, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(_number, $"Volume em porcentagem: {name}");
            _number.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            _number.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitNumber();
                e.Handled = true;
            };
            _number.LostKeyboardFocus += (_, _) => CommitNumber();
            numberPanel.Children.Add(_number);
            var percent = new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            percent.SetResourceReference(StyleProperty, "Caption");
            numberPanel.Children.Add(percent);
            Grid.SetColumn(numberPanel, 2);
            grid.Children.Add(numberPanel);
            Root = grid;
        }

        public AudioBusKind Kind { get; }
        public FrameworkElement Root { get; }

        private void CommitNumber()
        {
            if (!int.TryParse(_number.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value))
            {
                _number.Text = ((int)Math.Round(_slider.Value)).ToString(CultureInfo.CurrentCulture);
                return;
            }
            value = Math.Clamp(value, 0, 100);
            _owner.Changed(this, value, _mute.IsChecked == true);
        }

        public void Show(AudioBus bus)
        {
            _slider.Value = bus.Percent;
            if (!_number.IsKeyboardFocusWithin) _number.Text = bus.Percent.ToString(CultureInfo.CurrentCulture);
            _mute.IsChecked = bus.Muted;
            _slider.Opacity = bus.Muted ? 0.5 : 1;
            _mute.ToolTip = bus.Muted ? $"{_name}: sem som (clique para voltar)" : $"Silenciar: {_name}";
        }
    }
}
