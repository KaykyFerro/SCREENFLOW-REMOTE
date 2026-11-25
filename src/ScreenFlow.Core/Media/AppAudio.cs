namespace ScreenFlow.Core.Media;

/// <summary>Um app cujo som fica fora da transmissão: o executável (é o que identifica) e o nome mostrado.</summary>
public sealed record MutedApp(string Exe, string Name)
{
    public const int MaxCount = 24;

    /// <summary>"C:\Apps\Spotify.EXE" → "spotify.exe".</summary>
    public static string NormalizeExe(string? exe)
    {
        var name = Path.GetFileName((exe ?? string.Empty).Trim().Replace('/', '\\').Split('\\')[^1]).Trim();
        return name.ToLowerInvariant();
    }

    /// <summary>Nome para mostrar quando o app não diz o próprio nome: "spotify.exe" → "Spotify".</summary>
    public static string DefaultName(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(NormalizeExe(exe));
        return name.Length == 0 ? exe : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>Limpa a lista salva: sem vazios, sem repetidos, nomes preenchidos e no máximo <see cref="MaxCount"/>.</summary>
    public static List<MutedApp> Normalize(IEnumerable<MutedApp?>? apps) =>
        (apps ?? [])
        .Where(a => a is not null && NormalizeExe(a.Exe).Length is > 0 and <= 260)
        .Select(a => new MutedApp(NormalizeExe(a!.Exe), string.IsNullOrWhiteSpace(a.Name) ? DefaultName(a.Exe) : a.Name.Trim()))
        .DistinctBy(a => a.Exe)
        .Take(MaxCount)
        .ToList();

    /// <summary>"Spotify", "Spotify e Discord", "Spotify, Discord e Chrome".</summary>
    public static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " e " + names[^1],
    };
}

/// <summary>Um processo do Windows: id, pai, executável (em minúsculas) e quando começou (0 = não deu para ler).</summary>
public readonly record struct ProcessEntry(int Id, int ParentId, string Exe, long StartedAt = 0);

/// <summary>
/// Escolhe quais processos capturar para ter "todo o som do PC menos estes apps e o ScreenFlow". O Windows só tira uma
/// árvore de processos por captura, então cada app que tem som é capturado separado (com a árvore dele) e a mistura é
/// feita no ScreenFlow. Regras: o ScreenFlow e os apps tirados nunca entram; os "ancestrais" deles também não (capturar a
/// árvore de um ancestral, como o Explorer, traria o som deles junto); e um processo cujo ancestral já entra fica de fora
/// (a árvore do ancestral já cobre o som dele, e capturar os dois dobraria o volume).
/// </summary>
public static class AppAudioPlan
{
    /// <summary>Ids abaixo disso são do próprio Windows (0 = sons do sistema, 4 = System).</summary>
    private const int FirstUserProcessId = 8;

    /// <param name="sessionProcesses">Processos com sessão de som nas saídas ligadas (tocando ou parados).</param>
    /// <param name="processes">Tabela de processos do Windows.</param>
    /// <param name="ownProcessId">O ScreenFlow.</param>
    /// <param name="mutedExes">Executáveis que ficam de fora (em minúsculas, como em <see cref="MutedApp.NormalizeExe"/>).</param>
    public static IReadOnlyList<int> Choose(IEnumerable<int> sessionProcesses, IReadOnlyDictionary<int, ProcessEntry> processes, int ownProcessId,
        IReadOnlySet<string> mutedExes)
    {
        var muted = new HashSet<int> { ownProcessId };
        foreach (var process in processes.Values)
            if (mutedExes.Contains(process.Exe)) muted.Add(process.Id);
        var blocked = new HashSet<int>(muted);
        foreach (var id in muted)
            foreach (var ancestor in Ancestors(id, processes)) blocked.Add(ancestor);

        var candidates = new HashSet<int>();
        foreach (var id in sessionProcesses)
            if (id >= FirstUserProcessId && !blocked.Contains(id) && processes.ContainsKey(id)) candidates.Add(id);
        // Dois processos que parecem pai um do outro (ids reaproveitados sem a hora de início) não se cobrem: ficam os dois.
        return candidates
            .Where(id => !Ancestors(id, processes).Any(a => candidates.Contains(a) && !Ancestors(a, processes).Contains(id)))
            .OrderBy(id => id)
            .ToList();
    }

    /// <summary>
    /// Pai, avô e assim por diante. Um pai que começou depois do filho é outro processo que reaproveitou o id de um pai
    /// que já fechou: a corrente para ali.
    /// </summary>
    public static IEnumerable<int> Ancestors(int id, IReadOnlyDictionary<int, ProcessEntry> processes)
    {
        var seen = new HashSet<int> { id };
        if (!processes.TryGetValue(id, out var current)) yield break;
        for (var depth = 0; depth < 64; depth++)
        {
            if (current.ParentId < FirstUserProcessId || !seen.Add(current.ParentId) || !processes.TryGetValue(current.ParentId, out var parent)) yield break;
            if (parent.StartedAt != 0 && current.StartedAt != 0 && parent.StartedAt > current.StartedAt) yield break;
            yield return parent.Id;
            current = parent;
        }
    }
}

/// <summary>
/// Fila de um app na mistura do som do PC (áudio intercalado). Só começa a entregar com <c>prebufferFrames</c> guardados,
/// para absorver a diferença de chegada entre as capturas; se esvaziar, volta a esperar; se acumular demais (a captura
/// atrasou e chegou tudo de uma vez), joga fora o mais antigo.
/// </summary>
public sealed class MixQueue
{
    private readonly int _channels;
    private readonly int _prebuffer;
    private readonly int _max;
    private float[] _samples;
    private int _start;
    private int _count;
    private bool _playing;

    public MixQueue(int channels, int prebufferFrames, int maxFrames)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (prebufferFrames < 0 || maxFrames < Math.Max(1, prebufferFrames)) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        _channels = channels;
        _prebuffer = prebufferFrames;
        _max = maxFrames;
        _samples = new float[maxFrames * channels];
    }

    public int BufferedFrames => _count / _channels;
    public bool Playing => _playing;

    /// <summary>Amostras descartadas por excesso (para o log).</summary>
    public long DroppedFrames { get; private set; }

    public void Push(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / _channels;
        if (frames == 0) return;
        if (frames > _max)
        {
            DroppedFrames += frames - _max;
            samples = samples[((frames - _max) * _channels)..];
            frames = _max;
        }
        if (BufferedFrames + frames > _max)
        {
            // Volta para perto do ponto de partida (o prebuffer): o atraso não fica crescendo.
            var keep = Math.Clamp(_prebuffer - frames, 0, BufferedFrames);
            var drop = BufferedFrames - keep;
            Skip(drop);
            DroppedFrames += drop;
        }
        var length = frames * _channels;
        var end = (_start + _count) % _samples.Length;
        var first = Math.Min(length, _samples.Length - end);
        samples[..first].CopyTo(_samples.AsSpan(end));
        samples[first..length].CopyTo(_samples);
        _count += length;
    }

    /// <summary>Soma até <paramref name="frames"/> quadros em <paramref name="output"/>. Retorna quantos vieram da fila.</summary>
    public int MixInto(Span<float> output, int frames)
    {
        if (!_playing)
        {
            if (BufferedFrames < Math.Max(1, _prebuffer)) return 0;
            _playing = true;
        }
        var take = Math.Min(frames, BufferedFrames);
        var length = take * _channels;
        var first = Math.Min(length, _samples.Length - _start);
        Add(output, _samples.AsSpan(_start, first));
        Add(output[first..], _samples.AsSpan(0, length - first));
        Skip(take);
        if (take < frames) _playing = false;
        return take;
    }

    private void Skip(int frames)
    {
        var length = frames * _channels;
        _start = (_start + length) % _samples.Length;
        _count -= length;
        if (_count == 0) _start = 0;
    }

    private static void Add(Span<float> destination, ReadOnlySpan<float> source)
    {
        for (var i = 0; i < source.Length; i++) destination[i] += source[i];
    }
}
