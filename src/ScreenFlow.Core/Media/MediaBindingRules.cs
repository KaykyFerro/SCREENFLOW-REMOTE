namespace ScreenFlow.Core.Media;

/// <summary>
/// Qual sessão de mídia do Windows fica ligada à transmissão (1.4.0). Os comandos de quem assiste (e a minutagem) só vão
/// para uma sessão ligada, nunca para uma escolhida só por ser a mais recente.
/// </summary>
public static class MediaBindingRules
{
    /// <summary>
    /// Ligada: a que quem transmite escolheu (<paramref name="pinnedId"/>); senão, transmitindo uma janela ou a festa de um
    /// app só (<paramref name="sharedExe"/>), a desse app; senão (tela inteira, câmera, festa com todo o som), a única sessão
    /// cujo som vai junto (<paramref name="shareable"/>). Sem nenhuma ligada, devolve uma para mostrar só a quem transmite
    /// (a das teclas de mídia, a primeira tocando ou a primeira), com <c>Bound</c> falso.
    /// </summary>
    public static (T? Info, bool Bound) Bind<T>(IReadOnlyList<T> sessions, Func<T, string> idOf, Func<T, string> appIdOf, string? currentId,
        string? sharedExe, string? pinnedId, Func<T, bool> shareable, Func<T, bool> playing) where T : class
    {
        if (sessions.Count == 0) return (null, false);
        if (pinnedId is not null && sessions.FirstOrDefault(s => idOf(s) == pinnedId) is { } pinned) return (pinned, true);
        if (sharedExe is not null)
        {
            if (sessions.FirstOrDefault(s => MediaApps.Matches(appIdOf(s), sharedExe)) is { } shared) return (shared, true);
        }
        else
        {
            var candidates = sessions.Where(shareable).ToList();
            if (candidates.Count == 1) return (candidates[0], true);
        }
        var shown = sessions.FirstOrDefault(s => idOf(s) == currentId) ?? sessions.FirstOrDefault(playing) ?? sessions[0];
        return (shown, false);
    }
}
