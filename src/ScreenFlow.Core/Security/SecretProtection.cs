using System.Text;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Core.Security;

/// <summary>Cifra e decifra segredos guardados neste PC (no Windows, o DPAPI da conta do Windows).</summary>
public interface ISecretProtector
{
    /// <summary>O prefixo gravado antes do segredo cifrado ("dpapi"), para saber quem abre.</summary>
    string Scheme { get; }

    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>
/// (1.5.0) Senhas, PIN e convites nunca vão em texto puro para as configurações: são gravados como
/// "esquema:base64" cifrado pelo protetor registrado (no app, o DPAPI do Windows: só esta conta, neste PC, abre).
/// Sem protetor registrado (testes no Linux, por exemplo), o segredo simplesmente não é gravado.
/// </summary>
public static class SecretProtection
{
    private static volatile ISecretProtector? _protector;
    private static int _warnedMissing;

    /// <summary>Registra quem cifra (o app faz isso antes de abrir as configurações). Null tira o protetor.</summary>
    public static void Register(ISecretProtector? protector) => _protector = protector;

    public static bool Available => _protector is not null;

    /// <summary>O segredo cifrado para gravar, ou null (sem segredo, ou sem como cifrar).</summary>
    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        var protector = _protector;
        if (protector is null)
        {
            if (Interlocked.Exchange(ref _warnedMissing, 1) == 0)
                Log.Warn("Sem como cifrar senhas neste sistema: elas valem só enquanto o app está aberto.");
            return null;
        }
        try
        {
            return $"{protector.Scheme}:{Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(secret)))}";
        }
        catch (Exception exception)
        {
            Log.Warn($"Não deu para cifrar uma senha das configurações: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Abre um segredo gravado por <see cref="Protect"/>. Null quando não abre (configurações copiadas de outro PC ou de
    /// outra conta do Windows): a pessoa digita a senha de novo.
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        var protector = _protector;
        var colon = stored.IndexOf(':');
        if (protector is null || colon <= 0 || !stored.AsSpan(0, colon).SequenceEqual(protector.Scheme)) return null;
        try
        {
            return Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(stored[(colon + 1)..])));
        }
        catch (Exception exception)
        {
            Log.Warn($"Uma senha guardada não abriu neste PC (configurações de outro PC ou de outra conta?): {exception.Message}");
            return null;
        }
    }
}
