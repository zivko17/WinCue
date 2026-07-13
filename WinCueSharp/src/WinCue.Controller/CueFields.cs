// =============================================================================
// CueFields.cs — Asignación de campos de un Cue por nombre snake_case
// =============================================================================
// El inspector edita campos por nombre ("continue_mode", "fade_in"…), igual
// que el dict `fields` de Python. Aquí se resuelven contra las propiedades de
// Cue usando sus atributos [JsonPropertyName] (misma fuente de verdad que la
// serialización), con conversión de tipos y parsing de enums SCREAMING_SNAKE.
// =============================================================================

using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using WinCue.Core;

namespace WinCue.Controller;

public static class CueFields
{
    private static readonly Dictionary<string, PropertyInfo> Props = BuildMap();

    private static Dictionary<string, PropertyInfo> BuildMap()
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var p in typeof(Cue).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (attr is not null && p.CanWrite)
                map[attr.Name] = p;
        }
        return map;
    }

    /// <summary>Nombres de campo editables (los snake_case del JSON).</summary>
    public static IReadOnlyCollection<string> Names => Props.Keys;

    /// <summary>Asigna un valor a un campo del cue por su nombre snake_case.
    /// Devuelve false si el campo no existe o el valor no convierte.</summary>
    public static bool Apply(Cue cue, string fieldName, object? value)
    {
        if (!Props.TryGetValue(fieldName, out var prop))
            return false;

        try
        {
            Type target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            // target_id y parent_id: null / "" / -1 → null
            if (Nullable.GetUnderlyingType(prop.PropertyType) is not null &&
                (value is null || value as string == "" ||
                 (value is int i && i == -1) || (value is long l && l == -1)))
            {
                prop.SetValue(cue, null);
                return true;
            }
            if (value is null)
                return false;

            object converted;
            if (target.IsEnum)
            {
                converted = value.GetType() == target
                    ? value
                    : ParseEnum(target, value.ToString() ?? "");
            }
            else if (target == typeof(bool))
                converted = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            else if (target == typeof(int))
                converted = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            else if (target == typeof(double))
                converted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            else if (target == typeof(string))
                converted = value.ToString() ?? "";
            else
                return false;

            prop.SetValue(cue, converted);
            return true;
        }
        catch (Exception exc) when (exc is InvalidCastException or FormatException
            or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Parsea "AUTO_CONTINUE" → AutoContinue, "note_on" → NoteOn…</summary>
    private static object ParseEnum(Type enumType, string text)
    {
        string pascal = string.Concat(
            text.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        return Enum.Parse(enumType, pascal, ignoreCase: true);
    }
}
