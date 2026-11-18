using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// An answer in words, to compare two answers whole: each object's public properties by name, a dictionary's entries in
/// the order of their keys - which is no part of what it answers - and a list's items in their own order, which is.
/// Properties named in <c>leaving</c> are left out wherever they appear, for answers of two generations or sessions that
/// must agree on everything but which one they are.
/// </summary>
internal static class AnswerText
{
    public static string Of(object? value, params string[] leaving)
    {
        var text = new StringBuilder();
        var left = new HashSet<string>(leaving, StringComparer.Ordinal);
        Write(value, 0);
        return text.ToString();

        void Write(object? item, int depth)
        {
            Assert.True(depth < 40, "An answer nests no deeper than this.");
            Type? type = item?.GetType();
            switch (item)
            {
                case null:
                    text.Append("null");
                    return;
                case string words:
                    text.Append('"').Append(words).Append('"');
                    return;
                case Type or MemberInfo or Delegate:
                    text.Append(item);
                    return;
                case IFormattable formattable when type!.IsPrimitive || type.IsEnum || item is decimal or Guid or DateTime or DateTimeOffset or TimeSpan:
                    text.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                    return;
                case bool flag:
                    text.Append(flag);
                    return;
                case IEnumerable sequence when type!.GetInterfaces().Any(face =>
                    face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)):
                    text.Append('{');
                    foreach ((string key, object? entry) in sequence.Cast<object>()
                        .Select(pair => (Key: Of(pair.GetType().GetProperty("Key")!.GetValue(pair), leaving), Value: pair.GetType().GetProperty("Value")!.GetValue(pair)))
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        text.Append(key).Append(':');
                        Write(entry, depth + 1);
                        text.Append(',');
                    }

                    text.Append('}');
                    return;
                case IEnumerable sequence:
                    text.Append('[');
                    foreach (object? element in sequence)
                    {
                        Write(element, depth + 1);
                        text.Append(',');
                    }

                    text.Append(']');
                    return;
            }

            text.Append(type!.Name).Append('{');
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0 && !left.Contains(property.Name))
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                text.Append(property.Name).Append('=');
                Write(property.GetValue(item), depth + 1);
                text.Append(';');
            }

            text.Append('}');
        }
    }
}
