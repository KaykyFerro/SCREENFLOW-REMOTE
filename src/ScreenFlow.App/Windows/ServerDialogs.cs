using System.Net;
using System.Windows;
using System.Windows.Controls;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.App.Windows;

/// <summary>
/// (1.5.0) As janelas dos servidores por rede do Radmin: adicionar (pela rede, pelo convite ou um novo), editar (nome,
/// rede e conexão), mudar o nome e mostrar o convite. Tudo no estilo dos outros diálogos do app.
/// </summary>
public static class ServerDialogs
{
    public enum AddMode
    {
        Network,
        Invite,
        Create,
    }

    /// <summary>Uma opção de "Rede usada": null é o Radmin VPN (automático), "*" todas as redes, o resto o nome do adaptador.</summary>
    public sealed record AdapterChoice(string? Value, string Text);

    // ───────────────────────── Adicionar ─────────────────────────

    /// <summary>
    /// Adiciona um servidor e já abre ele. Devolve o servidor adicionado (e como foi: <paramref name="chosen"/>), ou null
    /// quando a pessoa desistiu.
    /// </summary>
    public static RoomServer? Add(Window? owner, AddMode mode, out AddMode chosen)
    {
        var window = BuildAdd(owner, mode, out var result);
        window.ShowDialog();
        (var server, chosen) = result();
        return server;
    }

    /// <summary>Monta a janela de adicionar sem abrir (as capturas de tela do CI usam direto).</summary>
    /// <param name="demo">Capturas do CI: os campos já preenchidos.</param>
    internal static Window BuildAdd(Window? owner, AddMode mode, out Func<(RoomServer? Server, AddMode Mode)> result,
        (string Network, string Password, string Name)? demo = null)
    {
        RoomServer? added = null;
        var current = mode;
        result = () => (added, current);
        var app = Application.Current;

        var networkMode = Segment("Rede do Radmin");
        var inviteMode = Segment("Convite");
        var createMode = Segment("Criar um novo");
        var segments = new StackPanel { Orientation = Orientation.Horizontal };
        segments.Children.Add(networkMode);
        segments.Children.Add(inviteMode);
        segments.Children.Add(createMode);
        var switcher = new Border
        {
            Child = segments,
            CornerRadius = new CornerRadius(19),
            Padding = new Thickness(3),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 16),
        };
        switcher.SetResourceReference(Border.BackgroundProperty, "SurfaceRaised");
        switcher.SetResourceReference(Border.BorderBrushProperty, "Border");

        // Pela rede do Radmin: o mesmo nome e a mesma senha que todo mundo usa no Radmin.
        var networkBox = new TextBox { MaxLength = SpaceKeys.MaxNetworkNameLength, Tag = "ex.: amigos-do-cs" };
        var passwordBox = new PasswordBox { MaxLength = SpaceKeys.MaxPasswordLength };
        var noPassword = Caption("Sem senha, quem souber o nome da rede entra no servidor.", 6);
        noPassword.SetResourceReference(TextBlock.ForegroundProperty, "Amber");
        var networkPanel = new StackPanel();
        networkPanel.Children.Add(Label("Nome da rede no Radmin"));
        networkPanel.Children.Add(networkBox);
        networkPanel.Children.Add(Label("Senha da rede", 14));
        networkPanel.Children.Add(passwordBox);
        networkPanel.Children.Add(noPassword);
        networkPanel.Children.Add(Caption("Quem digitar o mesmo nome e a mesma senha cai no mesmo servidor, e só quem tem os dois vê e ouve o que acontece nele. A senha não fica guardada: vira a chave do servidor, protegida pelo Windows."));

        // Pelo convite.
        var inviteBox = new TextBox { MaxLength = 160, Tag = "SF2-…" };
        inviteBox.SetResourceReference(Control.FontFamilyProperty, "FontMono");
        var invitePanel = new StackPanel();
        invitePanel.Children.Add(Label("Convite"));
        invitePanel.Children.Add(inviteBox);
        invitePanel.Children.Add(Caption("Peça o convite para quem já está no servidor (botão direito no servidor › Copiar o convite)."));

        // Um novo.
        var createPanel = new StackPanel();
        createPanel.Children.Add(Caption("Um servidor novo, com uma chave só dele. Depois de criar, mande o convite para quem você quer nele.", 0));

        var nameLabel = Label("Nome que aparece para você", 16);
        var nameBox = new TextBox { MaxLength = RoomSpace.MaxNameLength, Tag = "ex.: Amigos do CS" };
        var connection = ConnectionFields(null, [], out var adapterBox, out var peersBox);
        var error = ErrorText();

        var extra = new StackPanel();
        extra.Children.Add(switcher);
        extra.Children.Add(networkPanel);
        extra.Children.Add(invitePanel);
        extra.Children.Add(createPanel);
        extra.Children.Add(nameLabel);
        extra.Children.Add(nameBox);
        extra.Children.Add(connection);
        extra.Children.Add(error);

        var window = Dialog.Create(Dialog.Usable(owner), "", "IconTileCyan", "Adicionar um servidor",
            "Um servidor é um grupo só de quem você quer, dentro de uma rede do Radmin: canais de texto e de voz, conversa e transmissões ficam só entre quem está nele, cifrados.",
            extra, "Adicionar", "Cancelar", null, out var ok);
        window.Width = 560;

        void Show(AddMode next)
        {
            current = next;
            networkPanel.Visibility = next == AddMode.Network ? Visibility.Visible : Visibility.Collapsed;
            invitePanel.Visibility = next == AddMode.Invite ? Visibility.Visible : Visibility.Collapsed;
            createPanel.Visibility = next == AddMode.Create ? Visibility.Visible : Visibility.Collapsed;
            nameLabel.Text = next == AddMode.Create ? "Nome do servidor" : "Nome que aparece para você (opcional)";
            ok.Content = next switch { AddMode.Invite => "Entrar", AddMode.Create => "Criar servidor", _ => "Adicionar" };
            error.Visibility = Visibility.Collapsed;
            Validate();
        }

        void Validate()
        {
            noPassword.Visibility = passwordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ok.IsEnabled = current switch
            {
                AddMode.Network => SpaceKeys.NormalizeNetworkName(networkBox.Text).Length > 0,
                AddMode.Invite => inviteBox.Text.Trim().Length > 0,
                _ => true,
            };
        }

        networkMode.Checked += (_, _) => Show(AddMode.Network);
        inviteMode.Checked += (_, _) => Show(AddMode.Invite);
        createMode.Checked += (_, _) => Show(AddMode.Create);
        networkBox.TextChanged += (_, _) => Validate();
        passwordBox.PasswordChanged += (_, _) => Validate();
        inviteBox.TextChanged += (_, _) => Validate();
        if (demo is { } sample)
        {
            networkBox.Text = sample.Network;
            passwordBox.Password = sample.Password;
            nameBox.Text = sample.Name;
        }
        (mode switch { AddMode.Invite => inviteMode, AddMode.Create => createMode, _ => networkMode }).IsChecked = true;
        Show(mode);

        ok.Click += async (_, _) =>
        {
            error.Visibility = Visibility.Collapsed;
            if (!TryPeers(peersBox.Text, out var peers, out var problem))
            {
                ShowError(error, problem);
                return;
            }
            var adapter = (adapterBox.SelectedItem as AdapterChoice)?.Value;
            var room = AppServices.Room;
            var label = ok.Content;
            ok.IsEnabled = false;
            ok.Content = "Abrindo…";
            (RoomServer? Server, string? Error) outcome;
            try
            {
                outcome = current switch
                {
                    AddMode.Network => await room.AddNetworkServerAsync(nameBox.Text, networkBox.Text, passwordBox.Password, adapter, peers),
                    AddMode.Invite => room.JoinInviteServer(inviteBox.Text, nameBox.Text, adapter, peers),
                    _ => room.CreateInviteServer(nameBox.Text, adapter, peers),
                };
            }
            catch (Exception exception)
            {
                Log.Warn($"Servidor não adicionado: {exception}");
                outcome = (null, "Não deu para adicionar o servidor. Tente de novo.");
            }
            ok.Content = label;
            ok.IsEnabled = true;
            if (outcome.Error is { } failure)
            {
                ShowError(error, failure);
                return;
            }
            added = outcome.Server;
            if (window.IsVisible) window.DialogResult = true;
        };
        window.Loaded += (_, _) => (current switch { AddMode.Invite => inviteBox, AddMode.Create => (Control)nameBox, _ => networkBox }).Focus();
        return window;
    }

    // ───────────────────────── Editar ─────────────────────────

    /// <summary>Edita o servidor (nome, rede e conexão). true quando algo foi salvo.</summary>
    public static bool Edit(Window? owner, RoomServer server)
    {
        var window = BuildEdit(owner, server, out var saved);
        window.ShowDialog();
        return saved();
    }

    /// <summary>Monta a janela de editar sem abrir (as capturas de tela do CI usam direto).</summary>
    internal static Window BuildEdit(Window? owner, RoomServer server, out Func<bool> saved)
    {
        var done = false;
        saved = () => done;
        var space = server.Space ?? throw new ArgumentException("A sala da rede não é editada aqui.", nameof(server));
        var needsKey = space.Secret is null;
        var byNetwork = space.KeySource == SpaceKeySource.Network;

        var nameBox = new TextBox { MaxLength = RoomSpace.MaxNameLength, Text = space.Name };
        var extra = new StackPanel();
        extra.Children.Add(Label("Nome que aparece para você"));
        extra.Children.Add(nameBox);
        extra.Children.Add(Caption("Só muda aqui: cada pessoa dá o nome que quiser ao servidor."));

        TextBox? networkBox = null;
        PasswordBox? passwordBox = null;
        TextBox? inviteBox = null;
        if (byNetwork)
        {
            networkBox = new TextBox { MaxLength = SpaceKeys.MaxNetworkNameLength, Text = space.NetworkName ?? string.Empty };
            passwordBox = new PasswordBox { MaxLength = SpaceKeys.MaxPasswordLength };
            extra.Children.Add(Label("Nome da rede no Radmin", 16));
            extra.Children.Add(networkBox);
            extra.Children.Add(Label("Senha da rede", 14));
            extra.Children.Add(passwordBox);
            var hint = Caption(needsKey
                ? "A chave do servidor não abriu neste PC (ela fica protegida pela sua conta do Windows). Digite a senha da rede de novo para abrir."
                : "Para continuar na mesma rede, deixe a senha em branco. Para mudar de rede, digite o nome e a senha dela: os canais e a conversa guardados aqui do servidor antigo são apagados.");
            if (needsKey) hint.SetResourceReference(TextBlock.ForegroundProperty, "Amber");
            extra.Children.Add(hint);
        }
        else if (needsKey)
        {
            inviteBox = new TextBox { MaxLength = 160, Tag = "SF2-…" };
            inviteBox.SetResourceReference(Control.FontFamilyProperty, "FontMono");
            extra.Children.Add(Label("Convite", 16));
            extra.Children.Add(inviteBox);
            var hint = Caption("A chave do servidor não abriu neste PC (ela fica protegida pela sua conta do Windows). Cole o convite do servidor de novo para abrir.");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Amber");
            extra.Children.Add(hint);
        }

        var connection = ConnectionFields(space.Adapter, space.ManualPeers, out var adapterBox, out var peersBox);
        connection.Margin = new Thickness(0, 16, 0, 0);
        extra.Children.Add(connection);
        if (space.Fingerprint is { } fingerprint)
            extra.Children.Add(Caption($"Código de segurança do servidor: {fingerprint}. Quem está no mesmo servidor vê o mesmo código.", 14));
        var error = ErrorText();
        extra.Children.Add(error);

        var window = Dialog.Create(Dialog.Usable(owner), "", "IconTileCyan", $"Editar {space.Name}",
            "O nome, a rede do servidor e como este PC acha quem está nele.", extra, "Salvar", "Cancelar", null, out var ok);
        window.Width = 560;

        ok.Click += async (_, _) =>
        {
            error.Visibility = Visibility.Collapsed;
            if (!TryPeers(peersBox.Text, out var peers, out var problem))
            {
                ShowError(error, problem);
                return;
            }
            var label = ok.Content;
            ok.IsEnabled = false;
            ok.Content = "Salvando…";
            try
            {
                byte[]? secret = null;
                string? networkName = null;
                if (networkBox is not null && passwordBox is not null)
                {
                    var changed = SpaceKeys.NormalizeNetworkName(networkBox.Text) != SpaceKeys.NormalizeNetworkName(space.NetworkName);
                    if (changed || needsKey || passwordBox.Password.Length > 0)
                    {
                        if (SpaceKeys.NormalizeNetworkName(networkBox.Text).Length == 0)
                        {
                            ShowError(error, "Digite o nome da rede do Radmin.");
                            return;
                        }
                        secret = await RoomService.NetworkKeyAsync(networkBox.Text, passwordBox.Password);
                        networkName = networkBox.Text;
                    }
                }
                else if (inviteBox is not null && inviteBox.Text.Trim().Length > 0)
                {
                    if (!SpaceInvite.TryParse(inviteBox.Text, out var invite))
                    {
                        ShowError(error, "Esse convite não é válido. Confira se copiou o código inteiro (começa com SF2-).");
                        return;
                    }
                    secret = invite.Secret;
                }
                // Outra chave é outro servidor: avisa antes de apagar o que é do antigo.
                if (secret is not null && !needsKey && SpaceKeys.TagText(secret) != space.Tag
                    && !ConfirmDialog.Ask(window, "Mudar para outro servidor?",
                        "Com esse nome e essa senha, é o servidor de outra rede. Os canais e a conversa guardados aqui do servidor antigo são apagados deste PC (quem está nele continua com tudo).",
                        "Mudar", danger: true))
                    return;
                var adapter = (adapterBox.SelectedItem as AdapterChoice)?.Value;
                if (await AppServices.Room.EditServerAsync(server, new ServerEdit(nameBox.Text, secret, networkName, adapter, peers)) is { } failure)
                {
                    ShowError(error, failure);
                    return;
                }
                done = true;
                if (window.IsVisible) window.DialogResult = true;
            }
            catch (ArgumentException exception) { ShowError(error, exception.Message); }
            catch (Exception exception)
            {
                Log.Warn($"Servidor não editado: {exception}");
                ShowError(error, "Não deu para salvar. Tente de novo.");
            }
            finally
            {
                ok.Content = label;
                ok.IsEnabled = true;
            }
        };
        window.Loaded += (_, _) =>
        {
            if (needsKey) ((Control?)passwordBox ?? inviteBox)?.Focus();
            else
            {
                nameBox.Focus();
                nameBox.SelectAll();
            }
        };
        return window;
    }

    // ───────────────────────── Nome e convite ─────────────────────────

    /// <summary>O nome novo do servidor (só para você). Null quando a pessoa desistiu.</summary>
    public static string? AskName(Window? owner, RoomServer server)
    {
        var box = new TextBox { MaxLength = RoomSpace.MaxNameLength, Text = server.DisplayName };
        var window = Dialog.Create(Dialog.Usable(owner), "", "IconTileCyan", "Mudar o nome do servidor",
            "O nome só muda para você: cada pessoa dá o nome que quiser ao servidor.", box, "Mudar o nome", "Cancelar", null, out var ok);
        box.TextChanged += (_, _) => ok.IsEnabled = box.Text.Trim().Length > 0;
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return window.ShowDialog() == true ? box.Text : null;
    }

    /// <summary>Mostra o convite do servidor para copiar e mandar.</summary>
    public static void ShowInvite(Window? owner, RoomServer server, bool created = false)
    {
        var window = BuildInvite(owner, server, created, out var code);
        if (window is null) return;
        if (window.ShowDialog() != true) return;
        try { Clipboard.SetText(code); }
        catch (Exception exception) { Log.Warn($"Convite não copiado: {exception.Message}"); }
    }

    /// <summary>Monta a janela do convite sem abrir (as capturas de tela do CI usam direto). Null sem a chave.</summary>
    internal static Window? BuildInvite(Window? owner, RoomServer server, bool created, out string code)
    {
        code = server.Space?.InviteCode() ?? string.Empty;
        if (code.Length == 0) return null;
        var box = new TextBox { Text = code, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        box.SetResourceReference(Control.FontFamilyProperty, "FontMono");
        var extra = new StackPanel();
        extra.Children.Add(box);
        if (server.Space?.Fingerprint is { } fingerprint)
            extra.Children.Add(Caption($"Código de segurança: {fingerprint}. Quem entrar vê o mesmo código ao editar o servidor.", 8));
        var warning = Caption("Quem tem o convite entra no servidor: mande só para quem você quer nele.", 6);
        warning.SetResourceReference(TextBlock.ForegroundProperty, "Amber");
        extra.Children.Add(warning);
        var window = Dialog.Create(Dialog.Usable(owner), "", "IconTileViolet", created ? $"{server.DisplayName} foi criado" : $"Convite de {server.DisplayName}",
            "Mande este convite para quem você quer no servidor. A pessoa cola em Adicionar um servidor › Convite e entra.",
            extra, "Copiar o convite", "Fechar", null, out var ok);
        window.Width = 540;
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return window;
    }

    // ───────────────────────── Peças ─────────────────────────

    /// <summary>"Rede usada" e "IPs para chamar direto", escondidos atrás de "Opções de conexão".</summary>
    private static FrameworkElement ConnectionFields(string? adapter, IReadOnlyList<string> peers, out ComboBox adapterBox, out TextBox peersBox)
    {
        var app = Application.Current;
        var combo = adapterBox = new ComboBox { DisplayMemberPath = nameof(AdapterChoice.Text) };
        var choices = AdapterChoices(adapter);
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(c => string.Equals(c.Value, adapter, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        var box = peersBox = new TextBox { MaxLength = 600, Tag = "26.1.2.3, 26.4.5.6", Text = string.Join(", ", peers) };
        box.SetResourceReference(Control.FontFamilyProperty, "FontMono");

        var details = new StackPanel { Visibility = adapter is null && peers.Count == 0 ? Visibility.Collapsed : Visibility.Visible };
        details.Children.Add(Label("Rede usada", 10));
        details.Children.Add(combo);
        details.Children.Add(Caption("O Radmin VPN é o normal. Escolha outra rede (Tailscale, rede local) se o servidor for por ela."));
        details.Children.Add(Label("IPs para chamar direto (opcional)", 14));
        details.Children.Add(box);
        details.Children.Add(Caption("Para quando alguém do servidor não aparece sozinho. Separe com vírgula; a porta é opcional (26.1.2.3:47802)."));

        var toggle = new Button
        {
            Style = (Style)app.FindResource("LinkButton"),
            Content = details.Visibility == Visibility.Visible ? "Esconder as opções de conexão" : "Opções de conexão (rede usada e IPs)",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 0),
        };
        toggle.Click += (_, _) =>
        {
            var show = details.Visibility != Visibility.Visible;
            details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = show ? "Esconder as opções de conexão" : "Opções de conexão (rede usada e IPs)";
        };
        var panel = new StackPanel();
        panel.Children.Add(toggle);
        panel.Children.Add(details);
        return panel;
    }

    /// <summary>As redes que dá para escolher: o Radmin (automático), todas, e cada adaptador ligado agora.</summary>
    public static List<AdapterChoice> AdapterChoices(string? current)
    {
        var choices = new List<AdapterChoice> { new(null, "Radmin VPN (automático)"), new("*", "Todas as redes ligadas deste PC") };
        try
        {
            foreach (var adapter in RadminNetwork.GetIPv4Adapters())
            {
                if (!adapter.IsUp || IPAddress.IsLoopback(adapter.Address)) continue;
                if (choices.Any(c => string.Equals(c.Value, adapter.Name, StringComparison.OrdinalIgnoreCase))) continue;
                choices.Add(new AdapterChoice(adapter.Name, $"Só {adapter.Label}"));
            }
        }
        catch (Exception exception) { Log.Debug($"Adaptadores para o servidor: {exception.Message}"); }
        if (current is not null && choices.All(c => !string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase)))
            choices.Add(new AdapterChoice(current, $"Só {current} (desligado agora)"));
        return choices;
    }

    /// <summary>Lê os IPs digitados ("26.1.2.3, 26.4.5.6:47802"). false e o motivo quando algum não é um IP.</summary>
    public static bool TryPeers(string? text, out List<string> peers, out string problem)
    {
        peers = [];
        problem = string.Empty;
        foreach (var part in (text ?? string.Empty).Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (RoomSpace.CleanPeer(part) is not { } clean)
            {
                problem = $"\"{part}\" não é um IP (exemplo: 26.1.2.3 ou 26.1.2.3:47802).";
                return false;
            }
            if (!peers.Contains(clean)) peers.Add(clean);
        }
        if (peers.Count > RoomSpace.MaxManualPeers)
        {
            problem = $"No máximo {RoomSpace.MaxManualPeers} IPs.";
            return false;
        }
        return true;
    }

    private static RadioButton Segment(string text)
        => new() { Style = (Style)Application.Current.FindResource("Segment"), Content = text, GroupName = "server-add-mode" };

    private static TextBlock Label(string text, double top = 0)
    {
        var label = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("Label") };
        label.Margin = new Thickness(0, top, 0, 6);
        return label;
    }

    private static TextBlock Caption(string text, double top = 6)
        => new() { Text = text, Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, top, 0, 0) };

    private static TextBlock ErrorText()
    {
        var error = new TextBlock { Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        return error;
    }

    private static void ShowError(TextBlock error, string text)
    {
        error.Text = text;
        error.Visibility = Visibility.Visible;
    }
}
