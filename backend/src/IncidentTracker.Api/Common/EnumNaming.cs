using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IncidentTracker.Api.Common;

/// <summary>
/// Bản vẽ v3.1 (mục 5.2–5.4) ghi mọi giá trị enum dạng <c>UPPER_SNAKE</c> (<c>NOT_PLANNED</c>,
/// <c>TOO_HEATED</c>, <c>THUMBS_UP</c>...). Enum C# đặt tên PascalCase nên cần một chỗ duy nhất
/// dịch qua lại, dùng chung cho cột text trong PostgreSQL và cho JSON của API — hai bên vì
/// thế không bao giờ lệch nhau.
/// </summary>
public static class EnumNaming
{
    private static readonly ConcurrentDictionary<Type, (IReadOnlyDictionary<string, object> Parse, IReadOnlyDictionary<object, string> Format)> Cache = new();

    public static string ToUpperSnake(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(pascal[i - 1]) || char.IsDigit(pascal[i - 1])))
            {
                sb.Append('_');
            }
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    public static string Format<TEnum>(TEnum value) where TEnum : struct, Enum
        => Table<TEnum>().Format[value];

    public static TEnum Parse<TEnum>(string text) where TEnum : struct, Enum
        => TryParse<TEnum>(text, out var value)
            ? value
            : throw new ArgumentException($"'{text}' không phải giá trị hợp lệ của {typeof(TEnum).Name}.");

    public static bool TryParse<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var key = text.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        if (Table<TEnum>().Parse.TryGetValue(key, out var boxed))
        {
            value = (TEnum)boxed;
            return true;
        }

        return false;
    }

    /// <summary>Danh sách giá trị hợp lệ dạng text — dùng cho check constraint và thông báo lỗi.</summary>
    public static IReadOnlyList<string> Names<TEnum>() where TEnum : struct, Enum
        => Table<TEnum>().Format.Values.ToList();

    /// <summary>Sinh vế <c>col in ('A','B')</c> cho check constraint của PostgreSQL.</summary>
    public static string SqlInList<TEnum>(string column) where TEnum : struct, Enum
        => $"{column} in ({string.Join(",", Names<TEnum>().Select(n => $"'{n}'"))})";

    private static (IReadOnlyDictionary<string, object> Parse, IReadOnlyDictionary<object, string> Format) Table<TEnum>()
        where TEnum : struct, Enum
        => Cache.GetOrAdd(typeof(TEnum), static t =>
        {
            var parse = new Dictionary<string, object>(StringComparer.Ordinal);
            var format = new Dictionary<object, string>();
            foreach (var value in Enum.GetValues(t))
            {
                var name = ToUpperSnake(Enum.GetName(t, value)!);
                parse[name] = value;
                format[value] = name;
            }
            return (parse, format);
        });
}

/// <summary>EF Core value converter: enum ↔ text UPPER_SNAKE.</summary>
public sealed class UpperSnakeEnumConverter<TEnum> : ValueConverter<TEnum, string> where TEnum : struct, Enum
{
    public UpperSnakeEnumConverter()
        : base(v => EnumNaming.Format(v), s => EnumNaming.Parse<TEnum>(s)) { }
}

/// <summary>
/// Factory đặt <b>trước</b> <c>JsonStringEnumConverter</c> toàn cục trong <c>Converters</c>: STJ ưu tiên
/// converter trong options hơn attribute trên type, nên nếu không có factory này thì enum của module
/// Tickets sẽ bị serialize thành PascalCase ("NotPlanned") thay vì "NOT_PLANNED" như bản vẽ.
/// Chỉ nhận enum có gắn <c>[JsonConverter(typeof(UpperSnakeJsonConverter&lt;&gt;))]</c>.
/// </summary>
public sealed class UpperSnakeEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        // Chỉ nhận enum thuần; Nullable<TEnum> do STJ tự bọc bằng NullableConverter.
        if (!typeToConvert.IsEnum) return false;
        var attr = typeToConvert.GetCustomAttributes(typeof(JsonConverterAttribute), false).OfType<JsonConverterAttribute>().FirstOrDefault();
        return attr?.ConverterType is { IsGenericType: true } ct && ct.GetGenericTypeDefinition() == typeof(UpperSnakeJsonConverter<>);
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter?)Activator.CreateInstance(typeof(UpperSnakeJsonConverter<>).MakeGenericType(typeToConvert));
}

/// <summary>
/// System.Text.Json converter dùng qua attribute <c>[JsonConverter(typeof(UpperSnakeJsonConverter&lt;T&gt;))]</c>
/// trên từng enum của module Tickets, để không đụng tới enum R1 (Investigating/Resolved...)
/// vốn đang đi qua <c>JsonStringEnumConverter</c> mặc định.
/// </summary>
public sealed class UpperSnakeJsonConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), number);
        }

        var text = reader.GetString();
        return EnumNaming.TryParse<TEnum>(text, out var value)
            ? value
            : throw new JsonException(
                $"Giá trị '{text}' không hợp lệ cho {typeof(TEnum).Name}. Chấp nhận: {string.Join(", ", EnumNaming.Names<TEnum>())}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteStringValue(EnumNaming.Format(value));
}
