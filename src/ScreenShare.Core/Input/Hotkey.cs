namespace SocScreenShare.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// Un atajo global (empezar o parar), con los números de <c>RegisterHotKey</c>. Se guarda en los
/// ajustes como texto («Ctrl+Shift+F9») y se enseña en el idioma de la aplicación. Por defecto
/// Ctrl+Mayús+F9 empieza y Ctrl+Mayús+F10 para: no los usa Windows ni las aplicaciones habituales.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public const string DefaultStart = "Ctrl+Shift+F9";
    public const string DefaultStop = "Ctrl+Shift+F10";

    /// <summary>Un atajo pulsado en la casilla de configurarlo; null si esa tecla no se admite.</summary>
    public static Hotkey? TryCreate(HotkeyModifiers modifiers, int virtualKey) =>
        KeyCode(KeyName(virtualKey).ToUpperInvariant()) == virtualKey ? Parse(new Hotkey(modifiers, virtualKey).ToString()) : null;

    /// <summary>
    /// Atajos que ya son de Windows y no se pueden (o no se deben) quitar: bloquear, cambiar de
    /// ventana, cerrar, el menú Inicio, la captura de pantalla del sistema…
    /// </summary>
    public bool IsReserved =>
        Modifiers.HasFlag(HotkeyModifiers.Win)
        || (Modifiers == HotkeyModifiers.Alt && VirtualKey == 0x73)                                    // Alt+F4
        || (Modifiers == HotkeyModifiers.None && VirtualKey == 0x2C)                                   // Impr Pant
        || (Modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt) && VirtualKey == 0x2E)        // Ctrl+Alt+Supr
        || (Modifiers == HotkeyModifiers.None && VirtualKey is 0x70 or 0x7A);                          // F1 (ayuda), F11 (pantalla completa)

    /// <summary>
    /// ¿Valen los dos atajos juntos? Null si sí; si no, la clave de <c>Loc</c> del motivo (ilegible,
    /// reservado por Windows o el mismo para empezar y parar).
    /// </summary>
    public static string? ValidatePair(string? start, string? stop)
    {
        var a = Parse(start);
        var b = Parse(stop);
        if (a is null || b is null)
            return "ErrHotkeyInvalid";
        if (a.IsReserved || b.IsReserved)
            return "ErrHotkeyReserved";
        if (a == b)
            return "ErrHotkeySame";
        return null;
    }

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var mods = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": mods |= HotkeyModifiers.Control; continue;
                case "SHIFT" or "MAYÚS" or "MAYUS": mods |= HotkeyModifiers.Shift; continue;
                case "ALT": mods |= HotkeyModifiers.Alt; continue;
                case "WIN": mods |= HotkeyModifiers.Win; continue;
            }
            if (key is not null)
                return null;   // dos teclas que no son modificadores
            key = KeyCode(part);
            if (key is null)
                return null;
        }
        if (key is null)
            return null;
        // Una letra o un número sueltos se comerían esa tecla en todo Windows: necesitan modificador.
        var isFunction = key is >= 0x70 and <= 0x87;
        if (mods == HotkeyModifiers.None && !isFunction && key is not (0x13 or 0x2C))
            return null;
        return new Hotkey(mods, key.Value);
    }

    /// <summary>El texto que se guarda (siempre en inglés).</summary>
    public override string ToString() => Format(false);

    /// <summary>El texto que se enseña.</summary>
    public string Format(bool spanish)
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add(spanish ? "Mayús" : "Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    internal static int? KeyCode(string part)
    {
        if (part.Length == 1 && part[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            return part[0];
        if (part.Length >= 2 && part[0] == 'F' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
            return 0x6F + f;
        return part switch
        {
            "PAUSE" => 0x13,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PRINTSCREEN" or "PRTSC" => 0x2C,
            _ => null,
        };
    }

    internal static string KeyName(int vk) => vk switch
    {
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x13 => "Pause",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x2C => "PrintScreen",
        _ => ((char)vk).ToString(),
    };
}
